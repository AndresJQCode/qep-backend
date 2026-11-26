using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Authorization.Application;
using Modules.Authorization.Domain;
using Modules.Tenancy.Application;
using Npgsql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// La marca de siembra de roles y los roles que se guardan con ella (spec 2026-11-25, roles por tenant), contra
/// PostgreSQL: la atomicidad y la carrera entre dos siembras las deciden la transacción y los índices únicos.
/// </summary>
public sealed class TenantRoleSeedStoreTests(SharedTenancyHost host) : IClassFixture<SharedTenancyHost>
{
    private static readonly string[] ReadProducts = ["catalog.product.read"];

    // Uno por prueba (xUnit crea una instancia por prueba): las pruebas de la clase comparten el host.
    private readonly Guid _tenantId = Guid.CreateVersion7();

    [Fact]
    public async Task SavingWritesTheRolesAndTheMarkerTogether()
    {
        var factory = Factory();

        var saved = await SaveAsync(factory, "ventas", "bodega");

        Assert.True(saved);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantRoleSeedStore>();
        Assert.True(await store.IsSeededAsync(_tenantId, TestContext.Current.CancellationToken));
        Assert.Equal(
            ["bodega", "ventas"],
            (await store.RoleKeysAsync(_tenantId, TestContext.Current.CancellationToken)).Order(StringComparer.Ordinal));
        Assert.Contains(_tenantId, await store.SeededTenantIdsAsync(TestContext.Current.CancellationToken));

        using var client = Reader(factory);
        var roles = await client.GetFromJsonAsync<RolePayload[]>(
            $"/api/v1/tenants/{_tenantId}/authorization/roles",
            TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(roles!, role => role.Role == "ventas").IsSystem);
        Assert.False(Assert.Single(roles!, role => role.Role == "bodega").IsSystem);
    }

    [Fact]
    public async Task ASecondSaveForASeededTenantWritesNothing()
    {
        var factory = Factory();
        Assert.True(await SaveAsync(factory, "ventas"));

        var saved = await SaveAsync(factory, "otra");

        Assert.False(saved);
        Assert.Equal(["ventas"], await RoleKeysAsync(factory));
    }

    [Fact]
    public async Task AKeyTheTenantAlreadyHasCountsAsALostRaceAndWritesNoMarker()
    {
        var factory = Factory();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IRoleRepository>().Add(NewRole("ventas"));
            await scope.ServiceProvider.GetRequiredService<IAuthorizationUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var saved = await SaveAsync(factory, "ventas", "bodega");

        Assert.False(saved);
        Assert.Equal(["ventas"], await RoleKeysAsync(factory));
        Assert.Equal(0, await MarkerCountAsync());
    }

    [Fact]
    public async Task TwoConcurrentSavesEndWithOneSetOfRoles()
    {
        var factory = Factory();

        var results = await Task.WhenAll(
            SaveAsync(factory, "ventas", "bodega"),
            SaveAsync(factory, "ventas", "bodega"));

        Assert.Single(results, saved => saved);
        Assert.Equal(
            2,
            await CountAsync("""SELECT count(*) FROM "authorization".roles WHERE tenant_id = @tenant_id"""));
        Assert.Equal(1, await MarkerCountAsync());
    }

    [Fact]
    public async Task ASaveWithoutRolesStillWritesTheMarker()
    {
        var factory = Factory();

        var saved = await SaveAsync(factory);

        Assert.True(saved);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantRoleSeedStore>();
        Assert.True(await store.IsSeededAsync(_tenantId, TestContext.Current.CancellationToken));
    }

    // ---- helpers ----

    private QepApiFactory Factory() => host.Factory(connectionString => new QepApiFactory(connectionString));

    private Role NewRole(string key) =>
        Role.Create(RoleId.New(), _tenantId, key, key, string.Empty, ReadProducts, DateTimeOffset.UtcNow);

    /// <summary>Cada guardado en su propio scope, como dos requests o dos pods: nada del ChangeTracker se comparte.</summary>
    private async Task<bool> SaveAsync(QepApiFactory factory, params string[] keys)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantRoleSeedStore>();
        return await store.TrySaveAsync(
            _tenantId,
            keys.Select(NewRole).ToArray(),
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyCollection<string>> RoleKeysAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITenantRoleSeedStore>()
            .RoleKeysAsync(_tenantId, TestContext.Current.CancellationToken);
    }

    private Task<long> MarkerCountAsync() =>
        CountAsync("""SELECT count(*) FROM "authorization".role_seeds WHERE tenant_id = @tenant_id""");

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenant_id", _tenantId);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private HttpClient Reader(QepApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantId.ToString());
        client.DefaultRequestHeaders.Add("X-Permissions", TenancyPermissions.AdvisorshipRead);
        return client;
    }

    private sealed record RolePayload(string Role, bool IsSystem);

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            // Pinned, never inherited: the developer's user-secrets may turn the seed on.
            builder.UseSetting("Seed:Enabled", "false");
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
        }
    }
}
