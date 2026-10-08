using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Bootstrapper.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Modules.Identity.Infrastructure.Persistence;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Modules.Tenancy.Infrastructure.Seed;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

public sealed class SeedStartupTests
{
    // Literal y no TenancySeeder.OperatorTenantId: es el mismo valor que appsettings.json fija como
    // Platform:OperatorTenantId, y la prueba tiene que fallar si cualquiera de los dos se mueve solo.
    private static readonly Guid OperatorTenantId = Guid.Parse("01900000-0000-7000-8000-000000000006");

    private const string OperatorEmail = "operador@qcode.co";
    private const string Issuer = "https://accounts.google.com";
    private const string Audience = "test-audience";
    private static readonly byte[] SigningKeyBytes = RandomNumberGenerator.GetBytes(32);

    // La semilla crea un tenant y otorga admin. Que esté apagada por defecto es la única
    // defensa que tiene el ambiente desplegado, así que se prueba explícitamente.
    [Fact]
    public async Task SeedDisabledCreatesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: false);
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var seeded = await dbContext.Tenants
            .AnyAsync(tenant => tenant.Slug == "origen-botanico", TestContext.Current.CancellationToken);

        Assert.False(seeded);
    }

    // Prendida sin email no se puede sembrar la membresía, y un tenant al que nadie puede
    // entrar es peor que no sembrar nada. Mismo criterio que la cadena de conexión: fallar
    // con un mensaje que dice qué falta.
    [Fact]
    public async Task SeedEnabledWithoutOwnerEmailFailsStartup()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(
            database.GetConnectionString(), seedEnabled: true, ownerEmail: string.Empty);

        // ThrowsAny y no Throws<OptionsValidationException>: ValidateOnStart lanza durante
        // host.StartAsync(), y WebApplicationFactory puede entregarla envuelta. Lo que se afirma
        // es que el arranque muere y que el mensaje nombra la clave que falta, no el tipo exacto.
        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        Assert.Contains(messages, message => message.Contains("Seed:OwnerEmail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SeedCreatesTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenant = await dbContext.Tenants.SingleOrDefaultAsync(
            candidate => candidate.Id == new TenantId(TenancySeeder.SeedTenantId),
            TestContext.Current.CancellationToken);

        Assert.NotNull(tenant);
        Assert.Equal("origen-botanico", tenant.Slug);
        Assert.Equal("Origen botánico", tenant.DisplayName);
    }

    // El usuario nace sin proveedor vinculado a propósito: ProviderLinkingService lo vincula
    // solo en el primer login con Google, buscándolo por email verificado. Sembrarlo Invited
    // es exactamente lo que esa ruta espera encontrar.
    [Fact]
    public async Task SeedCreatesTheOwnerUser()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(
            database.GetConnectionString(), seedEnabled: true, ownerEmail: "Semilla@QCode.CO");
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.Email == "semilla@qcode.co",
            TestContext.Current.CancellationToken);

        Assert.NotNull(user);
    }

    // Sin membresía activa el tenant es invisible: ExternalClaimsTransformation resuelve los
    // permisos desde la membresía, así que un tenant sembrado sin ella devuelve 403 en todo.
    [Fact]
    public async Task SeedCreatesAnActiveAdminMembership()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var membership = await dbContext.Memberships.SingleOrDefaultAsync(
            candidate => candidate.TenantId == new TenantId(TenancySeeder.SeedTenantId),
            TestContext.Current.CancellationToken);

        Assert.NotNull(membership);
        Assert.Equal(MembershipState.Active, membership.State);
        Assert.Contains("admin", membership.Roles);
    }

    // Spec 2026-10-07, «Semilla»: el tenant de la semilla nace con los siete, pos incluido.
    [Fact]
    public async Task SeedEnablesTheSevenModules()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        Assert.Equal(
            ["catalog", "companies", "customers", "orders", "pos", "quotations", "reporting"],
            await SeedModuleKeysAsync(factory, expectedSource: TenantModuleSources.Seed));
    }

    // Sólo al crear: el seeder devuelve antes si el tenant ya existe, así que correrlo otra vez no
    // duplica (PK) ni resucita lo que alguien apagó a mano. Se apaga una fila entre las dos corridas
    // (inactiva, spec 2026-10-08 §3): si la segunda la reactivara, el módulo volvería a prenderse solo.
    [Fact]
    public async Task SeedingTwiceDoesNotDuplicateTheModules()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var seedTenant = new TenantId(TenancySeeder.SeedTenantId);
            var pos = TenantModuleKeys.Pos;
            var now = DateTimeOffset.UtcNow;
            await dbContext.TenantModules
                .Where(module => module.TenantId == seedTenant && module.ModuleKey == pos)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(module => module.Status, TenantModuleStatus.Inactive)
                        .SetProperty(module => module.StatusChangedAt, now),
                    TestContext.Current.CancellationToken);
        }

        await factory.Services.SeedTenantWithOwnerAsync(
            Guid.CreateVersion7(), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["catalog", "companies", "customers", "orders", "quotations", "reporting"],
            await SeedModuleKeysAsync(factory, expectedSource: TenantModuleSources.Seed));
    }

    // El tenant operador (QCode) se siembra después de Origen botánico, con id fijo: es el que
    // Platform:OperatorTenantId trae por defecto, así que la consola queda alcanzable sin tocar el
    // ConfigMap más allá del email.
    [Fact]
    public async Task SeedCreatesTheOperatorTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenant = await dbContext.Tenants.SingleOrDefaultAsync(
            candidate => candidate.Id == new TenantId(OperatorTenantId),
            TestContext.Current.CancellationToken);

        Assert.NotNull(tenant);
        Assert.Equal("qcode", tenant.Slug);
        Assert.Equal("QCode", tenant.DisplayName);
        Assert.Equal(TenantStatus.Active, tenant.Status);
    }

    // El dueño es Seed:OperatorOwnerEmail y no Seed:OwnerEmail: en producción ése puede ser el
    // email del cliente, y darle admin en QCode lo haría operador de toda la plataforma.
    [Fact]
    public async Task SeedGivesTheOperatorOwnerAnActiveAdminMembership()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(
            database.GetConnectionString(), seedEnabled: true, operatorOwnerEmail: "Operador@QCode.CO");
        using var client = factory.CreateClient();

        var operatorUserId = await UserIdAsync(factory, OperatorEmail);
        Assert.NotNull(operatorUserId);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenant = await dbContext.Tenants.SingleAsync(
            candidate => candidate.Id == new TenantId(OperatorTenantId),
            TestContext.Current.CancellationToken);
        var membership = await dbContext.Memberships.SingleAsync(
            candidate => candidate.TenantId == new TenantId(OperatorTenantId),
            TestContext.Current.CancellationToken);

        Assert.Equal(operatorUserId, membership.UserId);
        Assert.Equal(tenant.OwnerMembershipId, membership.Id);
        Assert.Equal(MembershipState.Active, membership.State);
        Assert.Contains("admin", membership.Roles);
    }

    [Fact]
    public async Task SeedEnablesTheSevenModulesInTheOperatorTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        Assert.Equal(
            ["catalog", "companies", "customers", "orders", "pos", "quotations", "reporting"],
            await SeedModuleKeysAsync(factory, expectedSource: TenantModuleSources.Seed, OperatorTenantId));
    }

    // Origen botánico sigue igual: su dueño es Seed:OwnerEmail, y el operador no recibe nada ahí.
    [Fact]
    public async Task TheOperatorSeedLeavesOrigenBotanicoAlone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        var ownerUserId = await UserIdAsync(factory, "semilla@qcode.co");
        var operatorUserId = await UserIdAsync(factory, OperatorEmail);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var memberships = await dbContext.Memberships
            .Where(candidate => candidate.TenantId == new TenantId(TenancySeeder.SeedTenantId))
            .ToListAsync(TestContext.Current.CancellationToken);

        var membership = Assert.Single(memberships);
        Assert.NotNull(ownerUserId);
        Assert.NotNull(operatorUserId);
        Assert.Equal(ownerUserId, membership.UserId);
        Assert.NotEqual(operatorUserId, membership.UserId);
        Assert.Equal(
            ["catalog", "companies", "customers", "orders", "pos", "quotations", "reporting"],
            await SeedModuleKeysAsync(factory, expectedSource: TenantModuleSources.Seed));
    }

    // Sin el email no hay a quién darle el tenant: no se crea, se advierte y el arranque sigue.
    // Es el estado del ConfigMap de producción hasta que el owner agregue la clave.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task WithoutTheOperatorOwnerEmailTheOperatorTenantIsNotCreated(string? operatorOwnerEmail)
    {
        await using var database = await StartDatabaseAsync();
        var logs = new CapturingLoggerProvider();
        using var factory = new QepApiFactory(
            database.GetConnectionString(), seedEnabled: true, operatorOwnerEmail: operatorOwnerEmail, logs: logs);
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        Assert.False(await dbContext.Tenants.AnyAsync(
            tenant => tenant.Id == new TenantId(OperatorTenantId) || tenant.Slug == "qcode",
            TestContext.Current.CancellationToken));
        Assert.True(await dbContext.Tenants.AnyAsync(
            tenant => tenant.Id == new TenantId(TenancySeeder.SeedTenantId),
            TestContext.Current.CancellationToken));
        var warning = Assert.Single(logs.Entries, entry => entry.EventId == 4102);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("Seed:OperatorOwnerEmail", warning.Message, StringComparison.Ordinal);
    }

    // Cada reinicio de pod corre la semilla: la segunda vez no duplica ni el tenant ni la membresía
    // ni los módulos.
    [Fact]
    public async Task SeedingTwiceDoesNotDuplicateTheOperatorTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        await factory.Services.RunQepSeedAsync(TestContext.Current.CancellationToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        Assert.Equal(1, await dbContext.Tenants.CountAsync(
            tenant => tenant.Slug == "qcode", TestContext.Current.CancellationToken));
        Assert.Equal(1, await dbContext.Memberships.CountAsync(
            membership => membership.TenantId == new TenantId(OperatorTenantId),
            TestContext.Current.CancellationToken));
        Assert.Equal(
            ["catalog", "companies", "customers", "orders", "pos", "quotations", "reporting"],
            await SeedModuleKeysAsync(factory, expectedSource: TenantModuleSources.Seed, OperatorTenantId));
    }

    // Si QCode ya se registró por el signup, el slug está ocupado por otro id. Crear el sembrado
    // reventaría contra IX_tenants_slug y tumbaría el arranque de cada pod: se salta y se advierte,
    // y no se crea el usuario operador, que quedaría sin membresía.
    [Fact]
    public async Task AQcodeSlugTakenByAnotherTenantSkipsTheOperatorTenant()
    {
        await using var database = await StartDatabaseAsync();
        var registeredQcodeId = Guid.CreateVersion7();
        using (var withoutSeed = new QepApiFactory(database.GetConnectionString(), seedEnabled: false))
        {
            using var bootstrap = withoutSeed.CreateClient();
            await withoutSeed.Services.SeedTenantWithOwnerAsync(
                registeredQcodeId, "qcode", "QCode registrado", Guid.CreateVersion7(),
                Membership.RegistrationOrigin, TestContext.Current.CancellationToken);
        }

        var logs = new CapturingLoggerProvider();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true, logs: logs);
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        Assert.False(await dbContext.Tenants.AnyAsync(
            tenant => tenant.Id == new TenantId(OperatorTenantId), TestContext.Current.CancellationToken));
        var qcode = await dbContext.Tenants.SingleAsync(
            tenant => tenant.Slug == "qcode", TestContext.Current.CancellationToken);
        Assert.Equal(registeredQcodeId, qcode.Id.Value);
        Assert.Null(await UserIdAsync(factory, OperatorEmail));
        var warning = Assert.Single(logs.Entries, entry => entry.EventId == 4103);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(registeredQcodeId.ToString(), warning.Message, StringComparison.Ordinal);
    }

    // De punta a punta y con la configuración por defecto: auth real (no el stub), sin fijar
    // Platform:OperatorTenantId, así que el valor sale de appsettings.json. El dueño sembrado entra
    // con Google y la consola le responde.
    [Fact]
    public async Task TheSeededOperatorOwnerReachesTheConsoleWithTheDefaultConfiguration()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(
            database.GetConnectionString(), seedEnabled: true, realAuthentication: true);
        // https: fuera de Development la cookie de sesión es Secure y sobre http no viaja
        // (ver RealAuthenticationApiTests.CreateClient).
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/session"))
        {
            login.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", IssueGoogleIdToken(Guid.NewGuid().ToString(), OperatorEmail));
            login.Headers.Add("X-Qep-Client", "web");
            var session = await client.SendAsync(login, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        }

        using var me = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/tenants/{OperatorTenantId}/authorization/me");
        me.Headers.Add("X-Tenant-Id", OperatorTenantId.ToString());
        var meResponse = await client.SendAsync(me, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var permissions = await meResponse.Content.ReadFromJsonAsync<EffectivePermissionsPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(permissions);
        Assert.Contains("operator.tenants.read", permissions.Permissions);
        Assert.Contains("operator.modules.manage", permissions.Permissions);
        Assert.Contains("operator.tenants.manage", permissions.Permissions);

        using var tenants = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/tenants/{OperatorTenantId}/operator/tenants");
        tenants.Headers.Add("X-Tenant-Id", OperatorTenantId.ToString());
        var tenantsResponse = await client.SendAsync(tenants, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, tenantsResponse.StatusCode);
    }

    private static async Task<Guid?> UserIdAsync(QepApiFactory factory, string normalizedEmail)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.Email == normalizedEmail, TestContext.Current.CancellationToken);
        return user?.Id.Value;
    }

    private static string IssueGoogleIdToken(string subject, string email)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(SigningKeyBytes), SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim("sub", subject),
                new Claim("email", email),
                new Claim("email_verified", "true"),
            ],
            notBefore: now.AddMinutes(-1),
            expires: now.AddMinutes(30),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed record EffectivePermissionsPayload(Guid TenantId, string[] Permissions);

    private static async Task<List<string>> SeedModuleKeysAsync(
        QepApiFactory factory, string expectedSource, Guid? tenantId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var seedTenant = new TenantId(tenantId ?? TenancySeeder.SeedTenantId);
        var rows = await dbContext.TenantModules
            .AsNoTracking()
            .Where(module => module.TenantId == seedTenant && module.Status == TenantModuleStatus.Active)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.All(rows, row => Assert.Equal(expectedSource, row.Source));
        return rows.Select(row => row.ModuleKey.Value).Order(StringComparer.Ordinal).ToList();
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

    private sealed class QepApiFactory(
        string connectionString,
        bool seedEnabled,
        string? ownerEmail = "semilla@qcode.co",
        string? operatorOwnerEmail = OperatorEmail,
        bool realAuthentication = false,
        CapturingLoggerProvider? logs = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Con auth real, un entorno que no es Development: ahí el stub queda apagado por
            // defecto y la sesión es la de producción (ver RealAuthenticationApiTests.QepApiFactory).
            builder.UseEnvironment(realAuthentication ? "IntegrationTests" : "Development");
            if (realAuthentication)
            {
                builder.UseSetting("Authentication:Audience", Audience);
                builder.UseSetting("Authentication:TestSigningKey", Convert.ToBase64String(SigningKeyBytes));
            }
            else
            {
                // Fijado salvo en la prueba de punta a punta, que existe justo para ejercitar el
                // default de appsettings.json: un id ajeno en los user-secrets de quien corre las
                // pruebas no debe decidir quién es operador.
                builder.UseSetting("Platform:OperatorTenantId", OperatorTenantId.ToString());
            }

            if (logs is not null)
            {
                builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(logs));
            }

            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, nunca heredado: con "infobip" y sus claves ausentes el validador de
            // Notifications falla al arrancar y todas las pruebas del archivo mueren antes
            // de su aserción.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Seed:Enabled", seedEnabled ? "true" : "false");
            builder.UseSetting("Seed:OwnerEmail", ownerEmail ?? string.Empty);
            // Siempre fijado, nunca heredado: un valor en los user-secrets cambiaría si se siembra
            // el tenant operador o no.
            builder.UseSetting("Seed:OperatorOwnerEmail", operatorOwnerEmail ?? string.Empty);
        }
    }

    private sealed record LogEntry(int EventId, LogLevel Level, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> entries = new();

        public IReadOnlyCollection<LogEntry> Entries => entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(eventId.Id, logLevel, formatter(state, exception)));
        }
    }
}
