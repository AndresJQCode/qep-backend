using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

public sealed class AuthSessionApiTests
{
    private const string SeededTenantId = "01900000-0000-7000-8000-000000000001";
    private const string AdminSubjectId = "01900000-0000-7000-8000-000000000002";
    private const string CustomRoleKey = "supervisor-ventas";
    private static readonly string[] DefaultRoles = ["advisor"];

    [Fact]
    public async Task FirstLoginLinksActivatesAndAcceptsInvitedMembership()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedSeededTenantAsync(factory);
        var email = NewEmail();
        var googleSubject = Guid.CreateVersion7().ToString();

        // Un admin invita el email al tenant sembrado.
        using (var admin = CreateAdminClient(factory))
        {
            var invite = await InviteAsync(admin, email);
            Assert.Equal(HttpStatusCode.Created, invite.StatusCode);
        }

        // El usuario invitado entra con Google (email verificado simulado).
        using var client = CreateLoginClient(factory, googleSubject, email, verified: true);
        var response = await client.PostAsync(
            "/api/v1/auth/session",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<SessionPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.Equal(email, session!.Email);
        Assert.Contains(Guid.Parse(SeededTenantId), session.ActiveTenantIds);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var user = await QueryRowAsync(
            connection,
            "SELECT status FROM identity.users WHERE email = @email",
            ("email", email));
        Assert.Equal("Active", user![0]);

        var link = await QueryRowAsync(
            connection,
            "SELECT subject FROM identity.provider_links WHERE provider = 'google' AND subject = @subject",
            ("subject", googleSubject));
        Assert.NotNull(link);

        var membership = await QueryRowAsync(
            connection,
            "SELECT state FROM tenancy.memberships WHERE user_id = @userId",
            ("userId", session.UserId));
        Assert.Equal("Active", membership![0]);
    }

    [Fact]
    public async Task LoginWithUninvitedEmailIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedSeededTenantAsync(factory);

        using var client = CreateLoginClient(
            factory,
            Guid.CreateVersion7().ToString(),
            NewEmail(),
            verified: true);
        var response = await client.PostAsync(
            "/api/v1/auth/session",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LoginWithUnverifiedEmailIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedSeededTenantAsync(factory);
        var email = NewEmail();

        using (var admin = CreateAdminClient(factory))
        {
            await InviteAsync(admin, email);
        }

        using var client = CreateLoginClient(
            factory,
            Guid.CreateVersion7().ToString(),
            email,
            verified: false);
        var response = await client.PostAsync(
            "/api/v1/auth/session",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RepeatedLoginIsIdempotent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedSeededTenantAsync(factory);
        var email = NewEmail();
        var googleSubject = Guid.CreateVersion7().ToString();

        using (var admin = CreateAdminClient(factory))
        {
            await InviteAsync(admin, email);
        }

        using var client = CreateLoginClient(factory, googleSubject, email, verified: true);

        var first = await client.PostAsync(
            "/api/v1/auth/session", null, TestContext.Current.CancellationToken);
        var second = await client.PostAsync(
            "/api/v1/auth/session", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstSession = await first.Content.ReadFromJsonAsync<SessionPayload>(
            TestContext.Current.CancellationToken);
        var secondSession = await second.Content.ReadFromJsonAsync<SessionPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(firstSession!.UserId, secondSession!.UserId);
        Assert.Contains(Guid.Parse(SeededTenantId), secondSession.ActiveTenantIds);
    }

    [Fact]
    public async Task LoginListsActiveTenantsWithDisplayNamesAndExcludesSuspendedMembership()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedSeededTenantAsync(factory);
        var email = NewEmail();
        var googleSubject = Guid.CreateVersion7().ToString();

        // Un segundo tenant activo y un tercero que se suspende después del login, sembrados
        // directo en la base — mismo patrón que usa SeedSeededTenantAsync para el tenant sembrado.
        var secondTenantId = Guid.CreateVersion7();
        var suspendedTenantId = Guid.CreateVersion7();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            dbContext.Tenants.Add(Tenant.Create(
                new TenantId(secondTenantId),
                "acme-consultoria",
                "Acme Consultoría",
                "es-CO",
                "America/Bogota",
                "yyyy-MM-dd",
                MembershipId.New(),
                DateTimeOffset.UtcNow));
            dbContext.Tenants.Add(Tenant.Create(
                new TenantId(suspendedTenantId),
                "zeta-ventures",
                "Zeta Ventures",
                "es-CO",
                "America/Bogota",
                "yyyy-MM-dd",
                MembershipId.New(),
                DateTimeOffset.UtcNow));
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var admin = CreateAdminClient(factory))
        {
            Assert.Equal(HttpStatusCode.Created, (await InviteAsync(admin, email)).StatusCode);
        }

        using (var adminSecond = CreateAdminClientForTenant(factory, secondTenantId.ToString()))
        {
            Assert.Equal(
                HttpStatusCode.Created,
                (await InviteAsync(adminSecond, email, secondTenantId.ToString())).StatusCode);
        }

        using (var adminSuspended = CreateAdminClientForTenant(factory, suspendedTenantId.ToString()))
        {
            Assert.Equal(
                HttpStatusCode.Created,
                (await InviteAsync(adminSuspended, email, suspendedTenantId.ToString())).StatusCode);
        }

        using var client = CreateLoginClient(factory, googleSubject, email, verified: true);
        var firstLogin = await client.PostAsync(
            "/api/v1/auth/session", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstLogin.StatusCode);
        var firstSession = await firstLogin.Content.ReadFromJsonAsync<SessionPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(firstSession);

        // Suspende la membresía del tercer tenant después de que el login la activó, para
        // probar que una membresía suspendida deja de listarse.
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var suspendedMembershipRow = await QueryRowAsync(
            connection,
            "SELECT id FROM tenancy.memberships WHERE tenant_id = @tenantId AND user_id = @userId",
            ("tenantId", suspendedTenantId),
            ("userId", firstSession!.UserId));
        Assert.NotNull(suspendedMembershipRow);

        using (var adminSuspended = CreateAdminClientForTenant(factory, suspendedTenantId.ToString()))
        {
            var suspend = await adminSuspended.PostAsync(
                $"/api/v1/tenants/{suspendedTenantId}/memberships/{suspendedMembershipRow![0]}/suspend",
                content: null,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        }

        var second = await client.PostAsync(
            "/api/v1/auth/session", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var session = await second.Content.ReadFromJsonAsync<SessionPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);

        Assert.Equal(2, session!.ActiveTenantIds.Count);
        Assert.Contains(Guid.Parse(SeededTenantId), session.ActiveTenantIds);
        Assert.Contains(secondTenantId, session.ActiveTenantIds);
        Assert.DoesNotContain(suspendedTenantId, session.ActiveTenantIds);

        Assert.Equal(2, session.ActiveTenants.Count);
        Assert.Contains(
            session.ActiveTenants,
            tenant => tenant.TenantId == Guid.Parse(SeededTenantId) && tenant.DisplayName == "QCode Demo");
        Assert.Contains(
            session.ActiveTenants,
            tenant => tenant.TenantId == secondTenantId && tenant.DisplayName == "Acme Consultoría");
        Assert.DoesNotContain(session.ActiveTenants, tenant => tenant.TenantId == suspendedTenantId);

        // Cada tenant lleva los roles de la membresía con el nombre del catálogo: las dos
        // invitaciones vivas de arriba usan DefaultRoles (advisor → "Asesor").
        var advisorOnly = new[] { new SessionRolePayload("advisor", "Asesor") };
        Assert.All(session.ActiveTenants, tenant =>
        {
            Assert.NotNull(tenant.Roles);
            Assert.Equal(advisorOnly, tenant.Roles);
        });

        // activeTenantIds y activeTenants salen de la misma consulta (ver AuthSessionEndpoints):
        // tienen que ser exactamente el mismo conjunto de ids. SetEquals y no Assert.Equal
        // porque el orden de un HashSet no está garantizado entre dos instancias distintas.
        Assert.True(
            session.ActiveTenants.Select(tenant => tenant.TenantId)
                .ToHashSet()
                .SetEquals(session.ActiveTenantIds));

        // Orden determinista: por DisplayName y, ante empate, por id.
        var expectedOrder = session.ActiveTenants
            .OrderBy(tenant => tenant.DisplayName, StringComparer.Ordinal)
            .ThenBy(tenant => tenant.TenantId)
            .Select(tenant => tenant.TenantId)
            .ToArray();
        Assert.Equal(expectedOrder, session.ActiveTenants.Select(tenant => tenant.TenantId));
    }

    [Fact]
    public async Task SessionListsRolesPerTenantWithCatalogDisplayNamesInStoredOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedSeededTenantAsync(factory);
        var email = NewEmail();
        var googleSubject = Guid.CreateVersion7().ToString();
        var seededTenantId = Guid.Parse(SeededTenantId);

        // Un rol custom del tenant: su nombre vive sólo en la base, no en el código, así que
        // prueba que el nombre sale de ITenantRoleCatalog y no del catálogo estático.
        using (var roleAdmin = CreateAdminClient(factory))
        {
            // El stub concede por defecto sólo los permisos de tenancy
            // (DevelopmentAuthenticationHandler.cs:61-79) y definir roles exige
            // advisorship.roles.manage (RoleEndpoints.cs:27-28).
            roleAdmin.DefaultRequestHeaders.Add(
                "X-Permissions",
                TenancyPermissions.AdvisorshipRolesManage);
            var createRole = await roleAdmin.PostAsJsonAsync(
                $"/api/v1/tenants/{SeededTenantId}/authorization/roles",
                new
                {
                    key = CustomRoleKey,
                    displayName = "Supervisor de ventas",
                    description = "Supervisa al equipo comercial.",
                    permissions = new[] { TenancyPermissions.SettingsRead },
                },
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, createRole.StatusCode);
        }

        // El custom primero y admin después: no es el orden alfabético ni el del catálogo
        // (sistema primero), así que sólo pasa si la respuesta respeta el orden guardado.
        using (var admin = CreateAdminClient(factory))
        {
            Assert.Equal(
                HttpStatusCode.Created,
                (await InviteAsync(admin, email, roles: [CustomRoleKey, "admin"])).StatusCode);
        }

        using var client = CreateLoginClient(factory, googleSubject, email, verified: true);
        var login = await client.PostAsync(
            "/api/v1/auth/session", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await login.Content.ReadFromJsonAsync<SessionPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.Equal(
            new[]
            {
                new SessionRolePayload(CustomRoleKey, "Supervisor de ventas"),
                new SessionRolePayload("admin", "Administrador"),
            },
            RolesOf(session!, seededTenantId));

        // Un rol retirado del catálogo que la membresía todavía nombra —el mismo caso que
        // TenantRoleCatalog.PermissionsForAsync tolera—. Va directo a la base porque la API no
        // deja invitar con un rol que el catálogo no conoce (InviteMember.cs:192-199).
        await using (var connection = new NpgsqlConnection(database.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var update = new NpgsqlCommand(
                "UPDATE tenancy.memberships SET roles = array_append(roles, 'rol-retirado') " +
                "WHERE user_id = @userId AND tenant_id = @tenantId",
                connection);
            update.Parameters.AddWithValue("userId", session!.UserId);
            update.Parameters.AddWithValue("tenantId", seededTenantId);
            Assert.Equal(
                1,
                await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        // Otro POST /auth/session y no GET /auth/me: con el stub de desarrollo el principal nunca
        // recibe qep_sub (ExternalClaimsTransformation.cs:31-36), así que /auth/me responde 401
        // acá. /auth/me se prueba con auth real en RealAuthenticationApiTests; el login es
        // idempotente (RepeatedLoginIsIdempotent) y arma la misma respuesta.
        var relogin = await client.PostAsync(
            "/api/v1/auth/session", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, relogin.StatusCode);
        var current = await relogin.Content.ReadFromJsonAsync<SessionPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(current);
        Assert.Equal(
            new[]
            {
                new SessionRolePayload(CustomRoleKey, "Supervisor de ventas"),
                new SessionRolePayload("admin", "Administrador"),
                // Sin nombre en el catálogo cae a la clave: el rol no se descarta.
                new SessionRolePayload("rol-retirado", "rol-retirado"),
            },
            RolesOf(current!, seededTenantId));
    }

    private static IReadOnlyList<SessionRolePayload> RolesOf(SessionPayload session, Guid tenantId)
    {
        var tenant = Assert.Single(session.ActiveTenants, tenant => tenant.TenantId == tenantId);
        Assert.NotNull(tenant.Roles);
        return tenant.Roles;
    }

    /// <summary>
    /// Desde el 2026-09-21 <c>TenancyDatabaseInitializer</c> ya no siembra ningún tenant: cada
    /// prueba tiene que crear el suyo. El <c>ownerMembershipId</c> es nuevo y nunca se persiste
    /// como membership — mismo patrón que ya usa esta clase para <c>secondTenantId</c> y
    /// <c>suspendedTenantId</c> más abajo—, así que no aparece en ningún roster ni infla sus
    /// conteos.
    /// </summary>
    private static async Task SeedSeededTenantAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        dbContext.Tenants.Add(Tenant.Create(
            new TenantId(Guid.Parse(SeededTenantId)),
            "qcode-demo",
            "QCode Demo",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            MembershipId.New(),
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static string NewEmail() => $"login-{Guid.NewGuid():N}@example.com";

    private static async Task<HttpResponseMessage> InviteAsync(
        HttpClient client,
        string email,
        string tenantId = SeededTenantId,
        string[]? roles = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email, displayName = "Ana Pérez", roles = roles ?? DefaultRoles })
        };
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string[]?> QueryRowAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return null;
        }

        var values = new string[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            values[index] = reader.GetValue(index).ToString() ?? string.Empty;
        }

        return values;
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private static HttpClient CreateAdminClient(QepApiFactory factory) =>
        CreateAdminClientForTenant(factory, SeededTenantId);

    private static HttpClient CreateAdminClientForTenant(QepApiFactory factory, string tenantId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", AdminSubjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        return client;
    }

    private static HttpClient CreateLoginClient(
        QepApiFactory factory,
        string googleSubject,
        string email,
        bool verified)
    {
        var client = factory.CreateClient();
        // X-Subject-Id lleva el subject del proveedor; X-Tenant-Id es un dummy que exige
        // el stub de auth de desarrollo. El login en sí es agnóstico del tenant.
        client.DefaultRequestHeaders.Add("X-Subject-Id", googleSubject);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", SeededTenantId);
        client.DefaultRequestHeaders.Add("X-Email", email);
        client.DefaultRequestHeaders.Add("X-Email-Verified", verified ? "true" : "false");
        return client;
    }

    private sealed record SessionPayload(
        Guid UserId,
        string? Email,
        IReadOnlyCollection<Guid> ActiveTenantIds,
        IReadOnlyCollection<ActiveTenantPayload> ActiveTenants);

    // Roles nullable a propósito: un backend que no manda el campo deserializa a null y la
    // prueba lo reporta como tal, en vez de reventar en el deserializador.
    private sealed record ActiveTenantPayload(
        Guid TenantId,
        string DisplayName,
        IReadOnlyList<SessionRolePayload>? Roles);

    private sealed record SessionRolePayload(string Role, string DisplayName);

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, no heredado: appsettings.json lleva el proveedor con el que se despliega el
            // producto, y una suite de integración que depende de eso termina dependiendo de las
            // credenciales de quien la corra. Con "infobip" y las claves de Infobip ausentes —CI,
            // un clon nuevo— NotificationsOptionsValidator falla al arrancar y todas las pruebas
            // del archivo mueren antes de llegar a su aserción.
            // El canal de log es el default de desarrollo (SDD-CT-03). SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}
