using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Integrations.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure;
using Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// La tabla <c>tenancy.tenant_providers</c> (plan 2026-10-21, Task 2): el puerto <see cref="ITenantProviders"/>,
/// el repositorio, las filas de proveedor del historial, los <c>CHECK</c> y el backfill de Zenvia de la
/// migración <c>AddTenantProviders</c>, contra Postgres de verdad.
/// </summary>
public sealed class TenantProvidersPersistenceTests
{
    private const string AddShopifyModuleKey = "20261018105954_AddShopifyModuleKey";
    private const string EmptyJson = "{}";
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EnabledReturnsOnlyActiveRows()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        var tenantId = await SeedTenantAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantProviderRepository>()
                .Add(TenantProvider.Create(tenantId, TenantProviderKeys.Zenvia, Now));
            await scope.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var before = await EnabledAsync(provider, tenantId.Value);
        Assert.NotNull(before);
        Assert.Equal(["zenvia"], before);

        await using (var scope = provider.CreateAsyncScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<ITenantProviderRepository>()
                .ListByTenantAsync(tenantId, TestContext.Current.CancellationToken);
            rows.Single().Deactivate(Now.AddHours(1));
            await scope.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var enabled = await EnabledAsync(provider, tenantId.Value);
        Assert.NotNull(enabled);
        Assert.Empty(enabled);
        await using var read = provider.CreateAsyncScope();
        var stored = Assert.Single(await read.ServiceProvider.GetRequiredService<ITenantProviderRepository>()
            .ListByTenantAsync(tenantId, TestContext.Current.CancellationToken));
        Assert.Equal(TenantModuleStatus.Inactive, stored.Status);
        Assert.Equal(Now, stored.EnabledAt);
        Assert.Equal(Now.AddHours(1), stored.StatusChangedAt);
    }

    // Mismo contrato que ITenantModules.FindAsync: el tenant que sólo existe en el stub de desarrollo.
    [Fact]
    public async Task UnknownTenantIsNull()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);

        Assert.Null(await EnabledAsync(provider, Guid.NewGuid()));
    }

    // D2: el registro no escribe nada, y un tenant sin fila tiene el proveedor deshabilitado.
    [Fact]
    public async Task NewTenantHasNoProviders()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add("X-Email", $"owner-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Add("X-Email-Verified", "true");

        var created = await client.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new { displayName = "Providers Org", slug = $"org-{Guid.NewGuid():N}"[..12] },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var registered = await created.Content.ReadFromJsonAsync<RegisterPayload>(TestContext.Current.CancellationToken);
        await using var scope = factory.Services.CreateAsyncScope();
        var enabled = await scope.ServiceProvider.GetRequiredService<ITenantProviders>()
            .FindEnabledAsync(registered!.TenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(enabled);
        Assert.Empty(enabled);
    }

    [Fact]
    public async Task ProviderChangeRowsPersist()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        var tenantId = await SeedTenantAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantChangeRepository>().Add(TenantChange.ForProvider(
                tenantId, Guid.CreateVersion7(), TenantProviderKeys.Zenvia, null, TenantModuleStatus.Active,
                ChangeReason.Contract, "contrato firmado", Guid.CreateVersion7(), Now));
            await scope.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = provider.CreateAsyncScope();
        var row = Assert.Single(await read.ServiceProvider.GetRequiredService<TenancyDbContext>().TenantChanges.AsNoTracking()
            .Where(change => change.TenantId == tenantId)
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TenantChangeKind.Provider, row.Kind);
        Assert.Equal("zenvia", row.ProviderKey);
        Assert.Null(row.ModuleKey);
        Assert.Null(row.FromStatus);
        Assert.Equal("active", row.ToStatus);
        Assert.Equal(ChangeReason.Contract, row.Reason);
    }

    // Dos habilitaciones simultáneas de un proveedor sin fila; sin la traducción sería un 500.
    [Fact]
    public async Task ADuplicateProviderRowIsNoChangesNotA500()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        var tenantId = await SeedTenantAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantProviderRepository>()
                .Add(TenantProvider.Create(tenantId, TenantProviderKeys.Zenvia, Now));
            await scope.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var late = provider.CreateAsyncScope();
        late.ServiceProvider.GetRequiredService<ITenantProviderRepository>()
            .Add(TenantProvider.Create(tenantId, TenantProviderKeys.Zenvia, Now));
        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            late.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.providers.no_changes", error.Code);
    }

    [Fact]
    public async Task TheCheckRejectsAnUnknownProviderKey()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        var tenantId = await SeedTenantAsync(provider);

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database.GetConnectionString(),
            $"INSERT INTO tenancy.tenant_providers (tenant_id, provider_key, status, enabled_at, status_changed_at) VALUES ('{tenantId.Value}', 'otro', 'active', now(), now());"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("CK_tenant_providers_provider_key", error.ConstraintName);
    }

    // Los CHECK del historial: provider_key va en las filas de proveedor y sólo ahí, module_key en las de
    // módulo y sólo ahí, y la clave es una del catálogo.
    [Theory]
    [InlineData("INSERT INTO tenancy.tenant_providers (tenant_id, provider_key, status, enabled_at, status_changed_at) VALUES ('{0}', 'zenvia', 'paused', now(), now());", "CK_tenant_providers_status")]
    [InlineData("INSERT INTO tenancy.tenant_changes (id, tenant_id, batch_id, kind, provider_key, to_status, reason, actor_user_id, occurred_at) VALUES (gen_random_uuid(), '{0}', gen_random_uuid(), 'provider', NULL, 'active', 'contract', gen_random_uuid(), now());", "CK_tenant_changes_provider_key")]
    [InlineData("INSERT INTO tenancy.tenant_changes (id, tenant_id, batch_id, kind, provider_key, to_status, reason, actor_user_id, occurred_at) VALUES (gen_random_uuid(), '{0}', gen_random_uuid(), 'provider', 'otro', 'active', 'contract', gen_random_uuid(), now());", "CK_tenant_changes_provider_key")]
    [InlineData("INSERT INTO tenancy.tenant_changes (id, tenant_id, batch_id, kind, module_key, provider_key, to_status, reason, actor_user_id, occurred_at) VALUES (gen_random_uuid(), '{0}', gen_random_uuid(), 'module', 'pos', 'zenvia', 'active', 'contract', gen_random_uuid(), now());", "CK_tenant_changes_provider_key")]
    [InlineData("INSERT INTO tenancy.tenant_changes (id, tenant_id, batch_id, kind, module_key, provider_key, to_status, reason, actor_user_id, occurred_at) VALUES (gen_random_uuid(), '{0}', gen_random_uuid(), 'provider', 'pos', 'zenvia', 'active', 'contract', gen_random_uuid(), now());", "CK_tenant_changes_module_key")]
    public async Task TheHistoryChecksRejectWhatThePlanForbids(string sql, string constraint)
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        var tenantId = await SeedTenantAsync(provider);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(database.GetConnectionString(), string.Format(CultureInfo.InvariantCulture, sql, tenantId.Value)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }

    // D14: zenvia activo en cada tenant con al menos una conexión de Zenvia, en cualquier estado, y nada en
    // el historial. Integrations se migra de verdad para que el SQL del backfill corra contra las columnas
    // reales de integrations.connections, no contra una copia.
    [Fact]
    public async Task TheBackfillEnablesZenviaForTenantsWithAZenviaConnection()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var tenancy = NewTenancyContext(connectionString);
        var migrator = tenancy.GetService<IMigrator>();
        await migrator.MigrateAsync(AddShopifyModuleKey, TestContext.Current.CancellationToken);
        await using (var integrations = NewIntegrationsContext(connectionString))
        {
            await integrations.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        var paused = Guid.CreateVersion7();
        var twoConnections = Guid.CreateVersion7();
        var otherProvider = Guid.CreateVersion7();
        var orphan = Guid.CreateVersion7();
        await ExecuteAsync(connectionString, $"""
            {LegacyTenantSql(paused)}
            {LegacyTenantSql(twoConnections)}
            {LegacyTenantSql(otherProvider)}
            {ConnectionSql(paused, "zenvia", "Paused")}
            {ConnectionSql(paused, "whatsapp-cloud", "Active")}
            {ConnectionSql(twoConnections, "zenvia", "Active")}
            {ConnectionSql(twoConnections, "zenvia", "NeedsAttention")}
            {ConnectionSql(otherProvider, "whatsapp-cloud", "Active")}
            {ConnectionSql(orphan, "zenvia", "Active")}
            """);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { (paused, "zenvia", "active"), (twoConnections, "zenvia", "active") }.Order(),
            (await ProviderRowsAsync(connectionString)).Order());
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM tenancy.tenant_changes"));
    }

    // Una base nueva: Integrations todavía no migró y el guard de to_regclass deja pasar la migración.
    [Fact]
    public async Task TheMigrationRunsWithoutTheIntegrationsTable()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var tenancy = NewTenancyContext(connectionString);

        await tenancy.GetService<IMigrator>().MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0L, await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'integrations' AND table_name = 'connections'"));
        Assert.Empty(await ProviderRowsAsync(connectionString));
    }

    // El Down borra antes las filas de proveedor del historial: con la columna fuera, el CHECK de kind de
    // antes las rechazaría.
    [Fact]
    public async Task DownDropsTheTableTheColumnAndTheProviderHistory()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        var tenantId = await SeedTenantAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantProviderRepository>()
                .Add(TenantProvider.Create(tenantId, TenantProviderKeys.Zenvia, Now));
            var changes = scope.ServiceProvider.GetRequiredService<ITenantChangeRepository>();
            changes.Add(TenantChange.ForProvider(tenantId, Guid.CreateVersion7(), TenantProviderKeys.Zenvia, null,
                TenantModuleStatus.Active, ChangeReason.Contract, null, Guid.CreateVersion7(), Now));
            changes.Add(TenantChange.ForModule(tenantId, Guid.CreateVersion7(), TenantModuleKeys.Pos, null,
                TenantModuleStatus.Active, ChangeReason.Courtesy, null, Guid.CreateVersion7(), Now));
            await scope.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var connectionString = database.GetConnectionString();
        await using var tenancy = NewTenancyContext(connectionString);
        await tenancy.GetService<IMigrator>().MigrateAsync(AddShopifyModuleKey, TestContext.Current.CancellationToken);

        Assert.Equal(0L, await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'tenancy' AND table_name = 'tenant_providers'"));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'tenancy' AND table_name = 'tenant_changes' AND column_name = 'provider_key'"));
        Assert.Equal("module", await ScalarAsync<string>(connectionString, "SELECT string_agg(kind, ',') FROM tenancy.tenant_changes"));
    }

    private static string LegacyTenantSql(Guid id) => $"""
        INSERT INTO tenancy.tenants (
            id, slug, status, display_name, default_culture, time_zone, date_format,
            owner_membership_id, version, created_at, updated_at)
        VALUES (
            '{id}', 't-{id:N}', 'Active', 'Backfill Test', 'es-CO', 'America/Bogota',
            'yyyy-MM-dd', '{Guid.CreateVersion7()}', 1, now(), now());
        """;

    // Columnas requeridas de integrations.connections (IntegrationsDbContextModelSnapshot.cs). El nombre es
    // único por tenant y proveedor (IX_connections_tenant_provider_name), así que lleva el estado.
    private static string ConnectionSql(Guid tenantId, string providerKey, string status) => $"""
        INSERT INTO integrations.connections (
            id, tenant_id, provider_key, name, status, fields, version, created_at, created_by, updated_at)
        VALUES (
            '{Guid.CreateVersion7()}', '{tenantId}', '{providerKey}', 'Conexión {status}', '{status}', '{EmptyJson}', 1, now(),
            '{Guid.CreateVersion7()}', now());
        """;

    private static async Task<List<(Guid TenantId, string ProviderKey, string Status)>> ProviderRowsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT tenant_id, provider_key, status FROM tenancy.tenant_providers", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<(Guid, string, string)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private static async Task<IReadOnlySet<string>?> EnabledAsync(ServiceProvider provider, Guid tenantId)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITenantProviders>()
            .FindEnabledAsync(tenantId, TestContext.Current.CancellationToken);
    }

    private static async Task<TenantId> SeedTenantAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenant = Tenant.Create(TenantId.New(), $"t-{Guid.NewGuid():N}"[..12], "Providers Test", "es-CO",
            "America/Bogota", "yyyy-MM-dd", MembershipId.New(), Now);
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return tenant.Id;
    }

    private static async Task<ServiceProvider> MigratedServicesAsync(PostgreSqlContainer database)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:QepDatabase"] = database.GetConnectionString() })
            .Build();
        var services = new ServiceCollection();
        services.AddTenancyInfrastructure(configuration);
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().Database.MigrateAsync(TestContext.Current.CancellationToken);
        return provider;
    }

    private static TenancyDbContext NewTenancyContext(string connectionString) =>
        new(new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "platform"))
            .Options);

    private static IntegrationsDbContext NewIntegrationsContext(string connectionString) =>
        new(new DbContextOptionsBuilder<IntegrationsDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", IntegrationsDbContext.Schema))
            .Options);

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
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

    private sealed record RegisterPayload(Guid TenantId, Guid OwnerUserId);

    private sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
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
            // Fijado, no heredado: ver RegistrationApiTests.QepApiFactory.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "true");
        }
    }
}
