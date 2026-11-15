using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Modules.Authorization.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using StackExchange.Redis;
using static Modules.Tenancy.IntegrationTests.TenancyTestDatabase;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// La caché de permisos en Valkey de punta a punta (spec 2026-11-13, D-5): con <c>Valkey:Areas:Permissions</c> prendido,
/// <see cref="IAuthorizationService.ResolvePermissionsAsync"/> guarda el resultado en <c>qep:perm:{userId}:{tenantId}</c> con la
/// versión de acceso del tenant con la que se construyó, y todo cambio que altera lo que alguien puede hacer en el tenant sube
/// <c>qep:tenantver:{tenantId}</c> antes del guardado y otra vez después. «Resolver» es llamar al servicio desde un scope del
/// host: el stub de auth de las peticiones no pasa por él. Un host, una base y un Valkey por clase; cada prueba registra su
/// propio tenant, salvo la que para Valkey, que levanta todo lo suyo.
/// </summary>
public sealed class CachedAuthorizationTests(CachedAuthorizationTests.Host host) : IClassFixture<CachedAuthorizationTests.Host>
{
    private static readonly Guid OperatorTenantId = Guid.Parse("01900000-0000-7000-8000-00000000c0de");

    private static readonly string[] ManagerPermissions =
        ["advisorship.invite", "advisorship.read", "advisorship.manage", "advisorship.roles.manage"];

    private static readonly string[] OperatorPermissions =
        ["operator.tenants.read", "operator.modules.manage", "operator.tenants.manage"];

    private static readonly string[] AdvisorRoles = ["advisor"];
    private static readonly string[] AdminRoles = ["admin"];
    private static readonly string[] ReadProducts = ["catalog.product.read"];
    private static readonly string[] ReadAndManageProducts = ["catalog.product.read", "catalog.product.manage"];
    private static readonly string[] StalePermissions = ["stale.permission"];

    /// <summary>2001-09-09 en milisegundos Unix: cualquier base de tiempo de hoy la supera, y ningún contador desde 1 la alcanza.</summary>
    private const long TimeBaseFloor = 1_000_000_000_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSecondResolveUsesTheCache()
    {
        var tenant = await RegisterAsync(host.Factory);
        Assert.NotNull(await ResolveAsync(host.Factory, tenant.OwnerUserId, tenant.TenantId));

        var commands = await host.Counter.CountAsync(() => ResolveAsync(host.Factory, tenant.OwnerUserId, tenant.TenantId));

        Assert.Equal(0, commands);
    }

    // Review Focus 3.
    [Fact]
    public async Task AnEntryBuiltWithAnOlderTenantVersionIsDiscarded()
    {
        var tenant = await RegisterAsync(host.Factory);
        Assert.NotNull(await ResolveAsync(host.Factory, tenant.OwnerUserId, tenant.TenantId));

        await host.Database.StringIncrementAsync(VersionKey(tenant.TenantId));
        var commands = await host.Counter.CountAsync(() => ResolveAsync(host.Factory, tenant.OwnerUserId, tenant.TenantId));

        Assert.True(commands > 0, "Una entrada con otra versión del tenant tiene que volver a Postgres.");
    }

    // Review Focus 3: la entrada que construyó un request con la versión del medio (la del primer bump) no sobrevive al
    // segundo.
    [Fact]
    public async Task AResolveRacingARoleChangeIsDiscardedByTheSecondBump()
    {
        var tenant = await RegisterAsync(host.Factory);
        using var owner = OwnerClient(host.Factory, tenant.TenantId);
        var role = await CreateRoleAsync(owner, tenant.TenantId, "ventas", ReadProducts);
        var member = await AddActiveMemberAsync(owner, tenant.TenantId, [role.Role]);
        var current = await CurrentVersionAsync(tenant.TenantId);
        await host.Database.StringSetAsync(
            PermissionKey(member.UserId, tenant.TenantId),
            JsonSerializer.Serialize(new { v = current + 1, p = ReadProducts }),
            TimeSpan.FromSeconds(60));

        Assert.Equal(HttpStatusCode.OK, (await UpdateRoleAsync(owner, tenant.TenantId, role, ReadAndManageProducts)).StatusCode);

        // La entrada sembrada lleva la versión del medio; sólo la descarta que haya habido dos bumps.
        Assert.Equal(current + 2, await CurrentVersionAsync(tenant.TenantId));
        Assert.Contains("catalog.product.manage", (await ResolveAsync(host.Factory, member.UserId, tenant.TenantId))!);
    }

    // Con allkeys-lru Valkey puede desalojar qep:tenantver y dejar viva una entrada construida con una versión vieja. Una versión
    // ausente no puede leerse como 0: una entrada con "v":0 (la de un tenant que nunca subió, o la que el contador vuelva a
    // alcanzar) se serviría con permisos de antes del desalojo.
    [Fact]
    public async Task AnEvictedTenantVersionIsNeverReadAsZero()
    {
        var tenant = await RegisterAsync(host.Factory);
        Assert.NotNull(await ResolveAsync(host.Factory, tenant.OwnerUserId, tenant.TenantId));
        await host.Database.StringSetAsync(
            PermissionKey(tenant.OwnerUserId, tenant.TenantId),
            JsonSerializer.Serialize(new { v = 0, p = StalePermissions }),
            TimeSpan.FromSeconds(60));
        await host.Database.KeyDeleteAsync(VersionKey(tenant.TenantId));

        var resolved = await ResolveAsync(host.Factory, tenant.OwnerUserId, tenant.TenantId);

        Assert.DoesNotContain("stale.permission", resolved!);
        Assert.True(
            await CurrentVersionAsync(tenant.TenantId) > TimeBaseFloor,
            "La versión ausente se siembra con una base de tiempo que no se puede volver a alcanzar.");
    }

    // Un bump sobre una versión desalojada también parte de la base de tiempo, no de 1.
    [Fact]
    public async Task ABumpOnAnEvictedVersionStartsFromATimeBase()
    {
        var tenant = await RegisterAsync(host.Factory);
        using var owner = OwnerClient(host.Factory, tenant.TenantId);
        await host.Database.KeyDeleteAsync(VersionKey(tenant.TenantId));

        await CreateRoleAsync(owner, tenant.TenantId, "ventas", ReadProducts);

        Assert.True(await CurrentVersionAsync(tenant.TenantId) > TimeBaseFloor);
        var ttl = await host.Database.KeyTimeToLiveAsync(VersionKey(tenant.TenantId));
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromDays(29), TimeSpan.FromDays(30));
    }

    [Fact]
    public async Task ANullResultIsCached()
    {
        var tenant = await RegisterAsync(host.Factory);
        var stranger = Guid.CreateVersion7();

        Assert.Null(await ResolveAsync(host.Factory, stranger, tenant.TenantId));

        var entry = await host.Database.StringGetAsync(PermissionKey(stranger, tenant.TenantId));
        Assert.False(entry.IsNull);
        using var json = JsonDocument.Parse((string)entry!);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("p").ValueKind);
        var ttl = await host.Database.KeyTimeToLiveAsync(PermissionKey(stranger, tenant.TenantId));
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task UpdatingARoleIsSeenImmediately()
    {
        var tenant = await RegisterAsync(host.Factory);
        using var owner = OwnerClient(host.Factory, tenant.TenantId);
        var role = await CreateRoleAsync(owner, tenant.TenantId, "ventas", ReadProducts);
        var member = await AddActiveMemberAsync(owner, tenant.TenantId, [role.Role]);
        Assert.DoesNotContain("catalog.product.manage", (await ResolveAsync(host.Factory, member.UserId, tenant.TenantId))!);

        Assert.Equal(HttpStatusCode.OK, (await UpdateRoleAsync(owner, tenant.TenantId, role, ReadAndManageProducts)).StatusCode);

        Assert.Contains("catalog.product.manage", (await ResolveAsync(host.Factory, member.UserId, tenant.TenantId))!);
    }

    // Un rol nuevo no lo tiene nadie todavía: lo que se ve es que las entradas del tenant se descartan.
    [Fact]
    public async Task CreatingARoleDiscardsTheCachedEntries()
    {
        var tenant = await RegisterAsync(host.Factory);
        using var owner = OwnerClient(host.Factory, tenant.TenantId);
        Assert.NotNull(await ResolveAsync(host.Factory, tenant.OwnerUserId, tenant.TenantId));
        var before = await CurrentVersionAsync(tenant.TenantId);

        await CreateRoleAsync(owner, tenant.TenantId, "ventas", ReadProducts);

        Assert.Equal(before + 2, await CurrentVersionAsync(tenant.TenantId));
        Assert.True(await host.Counter.CountAsync(() => ResolveAsync(host.Factory, tenant.OwnerUserId, tenant.TenantId)) > 0);
    }

    // Borrar un rol en uso se rechaza (RoleCommands): lo que se ve es que las entradas del tenant se descartan.
    [Fact]
    public async Task DeletingARoleDiscardsTheCachedEntries()
    {
        var tenant = await RegisterAsync(host.Factory);
        using var owner = OwnerClient(host.Factory, tenant.TenantId);
        var role = await CreateRoleAsync(owner, tenant.TenantId, "ventas", ReadProducts);
        Assert.NotNull(await ResolveAsync(host.Factory, tenant.OwnerUserId, tenant.TenantId));
        var before = await CurrentVersionAsync(tenant.TenantId);

        var deleted = await owner.DeleteAsync($"/api/v1/tenants/{tenant.TenantId}/authorization/roles/{role.Id}", Ct);

        Assert.True(deleted.IsSuccessStatusCode, $"DELETE respondió {deleted.StatusCode}.");
        Assert.Equal(before + 2, await CurrentVersionAsync(tenant.TenantId));
        Assert.True(await host.Counter.CountAsync(() => ResolveAsync(host.Factory, tenant.OwnerUserId, tenant.TenantId)) > 0);
    }

    [Fact]
    public async Task UpdatingMemberRolesIsSeenImmediately()
    {
        var tenant = await RegisterAsync(host.Factory);
        using var owner = OwnerClient(host.Factory, tenant.TenantId);
        var member = await AddActiveMemberAsync(owner, tenant.TenantId, AdvisorRoles);
        Assert.DoesNotContain("advisorship.manage", (await ResolveAsync(host.Factory, member.UserId, tenant.TenantId))!);

        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"/api/v1/tenants/{tenant.TenantId}/memberships/{member.Id}/roles")
        {
            Content = JsonContent.Create(new { roles = AdminRoles }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{await MembershipVersionAsync(member.Id)}\"");
        Assert.Equal(HttpStatusCode.OK, (await owner.SendAsync(request, Ct)).StatusCode);

        Assert.Contains("advisorship.manage", (await ResolveAsync(host.Factory, member.UserId, tenant.TenantId))!);
    }

    [Fact]
    public async Task SuspendingAMemberDeniesImmediately()
    {
        var tenant = await RegisterAsync(host.Factory);
        using var owner = OwnerClient(host.Factory, tenant.TenantId);
        var member = await AddActiveMemberAsync(owner, tenant.TenantId, AdvisorRoles);
        Assert.NotNull(await ResolveAsync(host.Factory, member.UserId, tenant.TenantId));

        Assert.Equal(HttpStatusCode.OK, (await MembershipActionAsync(owner, tenant.TenantId, member.Id, "suspend")).StatusCode);

        Assert.Null(await ResolveAsync(host.Factory, member.UserId, tenant.TenantId));
    }

    [Fact]
    public async Task RemovingAMemberDeniesImmediately()
    {
        var tenant = await RegisterAsync(host.Factory);
        using var owner = OwnerClient(host.Factory, tenant.TenantId);
        var member = await AddActiveMemberAsync(owner, tenant.TenantId, AdvisorRoles);
        Assert.NotNull(await ResolveAsync(host.Factory, member.UserId, tenant.TenantId));

        Assert.Equal(HttpStatusCode.OK, (await MembershipActionAsync(owner, tenant.TenantId, member.Id, "remove")).StatusCode);

        Assert.Null(await ResolveAsync(host.Factory, member.UserId, tenant.TenantId));
    }

    // El null cacheado de la suspensión se descarta: reactivar abre sin esperar los 60 s.
    [Fact]
    public async Task ReactivatingAMemberGrantsImmediately()
    {
        var tenant = await RegisterAsync(host.Factory);
        using var owner = OwnerClient(host.Factory, tenant.TenantId);
        var member = await AddActiveMemberAsync(owner, tenant.TenantId, AdvisorRoles);
        Assert.Equal(HttpStatusCode.OK, (await MembershipActionAsync(owner, tenant.TenantId, member.Id, "suspend")).StatusCode);
        Assert.Null(await ResolveAsync(host.Factory, member.UserId, tenant.TenantId));

        Assert.Equal(HttpStatusCode.OK, (await MembershipActionAsync(owner, tenant.TenantId, member.Id, "reactivate")).StatusCode);

        Assert.NotNull(await ResolveAsync(host.Factory, member.UserId, tenant.TenantId));
    }

    // El auto-accept del login (MembershipActivationService): el null cacheado de la invitación pendiente se descarta.
    [Fact]
    public async Task AcceptingAnInvitationGrantsImmediately()
    {
        var tenant = await RegisterAsync(host.Factory);
        using var owner = OwnerClient(host.Factory, tenant.TenantId);
        var invited = await InviteAsync(owner, tenant.TenantId, AdvisorRoles);
        Assert.Null(await ResolveAsync(host.Factory, invited.UserId, tenant.TenantId));

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMembershipActivation>()
                .AcceptInvitedMembershipsAsync(invited.UserId, "cached-authorization-tests", Ct);
        }

        Assert.NotNull(await ResolveAsync(host.Factory, invited.UserId, tenant.TenantId));
    }

    [Fact]
    public async Task ChangingTenantModulesMasksImmediately()
    {
        var tenant = await RegisterAsync(host.Factory);
        Assert.DoesNotContain(
            (await ResolveAsync(host.Factory, tenant.OwnerUserId, tenant.TenantId))!,
            permission => permission.StartsWith("pos.", StringComparison.Ordinal));
        using var operatorClient = StubClient(host.Factory, Guid.CreateVersion7(), OperatorTenantId, OperatorPermissions);

        var response = await operatorClient.PostAsJsonAsync(
            $"/api/v1/tenants/{OperatorTenantId}/operator/tenants/{tenant.TenantId}/modules/changes",
            new { changes = new[] { new { key = "pos", status = "active" } }, reason = "courtesy", note = (string?)null },
            Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains(
            (await ResolveAsync(host.Factory, tenant.OwnerUserId, tenant.TenantId))!,
            permission => permission.StartsWith("pos.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuspendingTheTenantDeniesImmediately()
    {
        var tenant = await RegisterAsync(host.Factory);
        Assert.NotNull(await ResolveAsync(host.Factory, tenant.OwnerUserId, tenant.TenantId));
        using var operatorClient = StubClient(host.Factory, Guid.CreateVersion7(), OperatorTenantId, OperatorPermissions);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/tenants/{OperatorTenantId}/operator/tenants/{tenant.TenantId}/status")
        {
            Content = JsonContent.Create(new { status = "inactive", reason = "nonpayment", note = (string?)null }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.OK, (await operatorClient.SendAsync(request, Ct)).StatusCode);

        Assert.Null(await ResolveAsync(host.Factory, tenant.OwnerUserId, tenant.TenantId));
    }

    // Contenedor y host propios: parar el Valkey de la clase dejaría sin caché al resto de las pruebas.
    [Fact]
    public async Task WithValkeyDownPermissionsComeFromPostgres()
    {
        await using var database = await StartDatabaseAsync();
        await using var valkey = await ValkeyTestServer.StartAsync(Ct);
        await using var factory = new CachedAuthorizationFactory(
            database.GetConnectionString(), ValkeyTestServer.ConnectionString(valkey), new CommandCounter());
        var tenant = await RegisterAsync(factory);
        var expected = await ResolveAsync(factory, tenant.OwnerUserId, tenant.TenantId);
        Assert.NotNull(expected);

        await valkey.StopAsync(Ct);
        using (var owner = OwnerClient(factory, tenant.TenantId))
        {
            Assert.Equal(HttpStatusCode.OK, (await MembershipActionAsync(
                owner, tenant.TenantId, (await AddActiveMemberAsync(owner, tenant.TenantId, AdvisorRoles, factory)).Id, "suspend"))
                .StatusCode);
        }

        Assert.Equal(expected.Order(), (await ResolveAsync(factory, tenant.OwnerUserId, tenant.TenantId))!.Order());
    }

    private static async Task<IReadOnlyCollection<string>?> ResolveAsync(
        WebApplicationFactory<Program> factory, Guid userId, Guid tenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .ResolvePermissionsAsync(userId, tenantId, Ct);
    }

    private async Task<long> CurrentVersionAsync(Guid tenantId)
    {
        var value = await host.Database.StringGetAsync(VersionKey(tenantId));
        return value.IsNull ? 0 : (long)value;
    }

    private static RedisKey PermissionKey(Guid userId, Guid tenantId) => $"qep:perm:{userId:D}:{tenantId:D}";

    private static RedisKey VersionKey(Guid tenantId) => $"qep:tenantver:{tenantId:D}";

    private static async Task<(Guid TenantId, Guid OwnerUserId)> RegisterAsync(WebApplicationFactory<Program> factory)
    {
        using var bootstrap = StubClient(factory, Guid.CreateVersion7(), Guid.CreateVersion7());
        bootstrap.DefaultRequestHeaders.Add("X-Email", $"owner-{Guid.CreateVersion7():N}@example.com");
        bootstrap.DefaultRequestHeaders.Add("X-Email-Verified", "true");
        var response = await bootstrap.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new
            {
                displayName = "Cache Test Org",
                slug = $"cache-{Guid.NewGuid():N}"[..14],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registered = await response.Content.ReadFromJsonAsync<RegisteredPayload>(Ct);
        Assert.NotNull(registered);
        return (registered.TenantId, registered.OwnerUserId);
    }

    private static HttpClient OwnerClient(WebApplicationFactory<Program> factory, Guid tenantId) =>
        StubClient(factory, Guid.CreateVersion7(), tenantId, ManagerPermissions);

    private static HttpClient StubClient(
        WebApplicationFactory<Program> factory, Guid subjectId, Guid tenantId, params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    private static async Task<MembershipPayload> InviteAsync(HttpClient owner, Guid tenantId, IReadOnlyCollection<string> roles)
    {
        var response = await owner.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/memberships",
            new { email = $"member-{Guid.NewGuid():N}@example.com", displayName = "Ana Pérez", roles, advisorCode = (int?)null },
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MembershipPayload>(Ct))!;
    }

    // Saltea la vuelta de invitar-aceptar, como MembershipLifecycleApiTests: la aceptación tiene su propia prueba.
    private async Task<MembershipPayload> AddActiveMemberAsync(
        HttpClient owner, Guid tenantId, IReadOnlyCollection<string> roles, WebApplicationFactory<Program>? factory = null)
    {
        var member = await InviteAsync(owner, tenantId, roles);
        var connectionString = factory is CachedAuthorizationFactory own ? own.ConnectionString : host.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "UPDATE tenancy.memberships SET state = 'Active', accepted_at = now() WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", member.Id);
        await command.ExecuteNonQueryAsync(Ct);
        return member;
    }

    private async Task<long> MembershipVersionAsync(Guid membershipId)
    {
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT version FROM tenancy.memberships WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", membershipId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<HttpResponseMessage> MembershipActionAsync(
        HttpClient owner, Guid tenantId, Guid membershipId, string action)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/tenants/{tenantId}/memberships/{membershipId}/{action}");
        return await owner.SendAsync(request, Ct);
    }

    private static async Task<RolePayload> CreateRoleAsync(
        HttpClient owner, Guid tenantId, string key, IReadOnlyCollection<string> permissions)
    {
        var response = await owner.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/authorization/roles",
            new { key, displayName = key, description = string.Empty, permissions },
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RolePayload>(Ct))!;
    }

    private static async Task<HttpResponseMessage> UpdateRoleAsync(
        HttpClient owner, Guid tenantId, RolePayload role, IReadOnlyCollection<string> permissions)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"/api/v1/tenants/{tenantId}/authorization/roles/{role.Id}")
        {
            Content = JsonContent.Create(new { displayName = role.DisplayName, description = string.Empty, permissions }),
        };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{role.Version}\""));
        return await owner.SendAsync(request, Ct);
    }

    private sealed record RegisteredPayload(Guid TenantId, Guid OwnerUserId);

    private sealed record MembershipPayload(Guid Id, Guid UserId);

    private sealed record RolePayload(Guid? Id, string Role, string DisplayName, long Version);

    /// <summary>El host de la clase: una base, un Valkey con el área de permisos prendida y una conexión directa a él.</summary>
    public sealed class Host : IAsyncLifetime
    {
        private TestDatabase? _database;
        private IContainer? _valkey;
        private ConnectionMultiplexer? _multiplexer;
        private CachedAuthorizationFactory? _factory;

        public CommandCounter Counter { get; } = new();

        public CachedAuthorizationFactory Factory => _factory!;

        public string ConnectionString => _database!.GetConnectionString();

        public IDatabase Database => _multiplexer!.GetDatabase();

        public async ValueTask InitializeAsync()
        {
            _database = await StartDatabaseAsync();
            _valkey = await ValkeyTestServer.StartAsync(TestContext.Current.CancellationToken);
            _multiplexer = await ConnectionMultiplexer.ConnectAsync(ValkeyTestServer.ConnectionString(_valkey));
            _factory = new CachedAuthorizationFactory(
                _database.GetConnectionString(), ValkeyTestServer.ConnectionString(_valkey), Counter);
        }

        public async ValueTask DisposeAsync()
        {
            if (_factory is not null)
            {
                await _factory.DisposeAsync();
            }

            if (_multiplexer is not null)
            {
                await _multiplexer.DisposeAsync();
            }

            if (_valkey is not null)
            {
                await _valkey.DisposeAsync();
            }

            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }
    }

    public sealed class CachedAuthorizationFactory(string connectionString, string valkeyConnectionString, CommandCounter counter)
        : WebApplicationFactory<Program>
    {
        public string ConnectionString => connectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "true");
            builder.UseSetting("Authentication:UseDevelopmentStub", "true");
            builder.UseSetting("Seed:Enabled", "false");
            builder.UseSetting("Platform:OperatorTenantId", OperatorTenantId.ToString());
            // Con UseSetting y no con ConfigureAppConfiguration: AddQepValkey decide qué registra al armar el contenedor.
            builder.UseSetting("Valkey:Enabled", "true");
            builder.UseSetting("Valkey:Areas:Permissions", "true");
            builder.UseSetting("ConnectionStrings:Valkey", valkeyConnectionString);
            builder.ConfigureServices(services =>
                services.ConfigureDbContext<TenancyDbContext>(options => options.AddInterceptors(counter)));
        }
    }

    /// <summary>
    /// Cuenta los comandos de <c>TenancyDbContext</c> sobre <c>tenancy.memberships</c>, sólo los del flujo asíncrono de la
    /// prueba que está midiendo: los workers del host también leen membresías y no pueden sumar.
    /// </summary>
    public sealed class CommandCounter : DbCommandInterceptor
    {
        private readonly AsyncLocal<bool> _measuring = new();
        private int _count;

        public async Task<int> CountAsync(Func<Task> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            Interlocked.Exchange(ref _count, 0);
            _measuring.Value = true;
            try
            {
                await action();
            }
            finally
            {
                _measuring.Value = false;
            }

            return Volatile.Read(ref _count);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Inspect(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Inspect(command);
            return result;
        }

        private void Inspect(DbCommand command)
        {
            if (_measuring.Value
                && (command.CommandText.Contains("tenancy.memberships", StringComparison.Ordinal)
                    || command.CommandText.Contains("\"tenancy\".\"memberships\"", StringComparison.Ordinal)))
            {
                Interlocked.Increment(ref _count);
            }
        }
    }
}
