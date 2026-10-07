using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure;
using Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// La tabla <c>tenancy.tenant_modules</c> (spec 2026-10-07, «Tabla y migración»): el backfill de la
/// misma migración, el <c>CHECK</c> y el adaptador <c>ITenantModules</c>. Migra sólo Tenancy con
/// <see cref="IMigrator"/>, mismo patrón que <c>AuthorizationPermissionsMigrationTests</c>.
/// </summary>
public sealed class TenantModulesMigrationTests
{
    private const string AddMembershipAdvisorCode = "20260924152521_AddMembershipAdvisorCode";
    private const string LegacyTenantId = "01900000-0000-7000-8000-00000000e001";

    // Columnas de tenancy.tenants en AddMembershipAdvisorCode (TenancyDbContextModelSnapshot.cs);
    // owner_membership_id es NOT NULL desde RequireTenantOwnerMembership y no tiene FK.
    private const string LegacyTenantSql = $"""
        INSERT INTO tenancy.tenants (
            id, slug, status, display_name, default_culture, time_zone, date_format,
            owner_membership_id, version, created_at, updated_at)
        VALUES (
            '{LegacyTenantId}', 'backfill-test', 'Active', 'Backfill Test', 'es-CO', 'America/Bogota',
            'yyyy-MM-dd', '01900000-0000-7000-8000-00000000e002', 1, now(), now());
        """;

    [Fact]
    public async Task AnExistingTenantKeepsTheSixModulesOfToday()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(AddMembershipAdvisorCode, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyTenantSql);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                ("catalog", "backfill"), ("companies", "backfill"), ("customers", "backfill"),
                ("orders", "backfill"), ("quotations", "backfill"), ("reporting", "backfill"),
            ],
            await RowsAsync(connectionString, LegacyTenantId));
    }

    [Fact]
    public async Task TheCheckRejectsAnUnknownKey()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        await context.GetService<IMigrator>().MigrateAsync(
            AddMembershipAdvisorCode, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyTenantSql);
        await context.GetService<IMigrator>().MigrateAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connectionString,
            $"""
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
            VALUES ('{LegacyTenantId}', 'inventory', now(), 'manual');
            """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    // El adaptador: null para un tenant sin fila en tenancy.tenants (el simulado por el stub) y
    // los efectivos para uno que existe, con las claves ya tipadas por la conversión de EF.
    [Fact]
    public async Task FindAsyncDistinguishesAMissingTenantFromOneWithModules()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using (var context = NewContext(connectionString))
        {
            await context.GetService<IMigrator>().MigrateAsync(
                AddMembershipAdvisorCode, TestContext.Current.CancellationToken);
            await ExecuteAsync(connectionString, LegacyTenantSql);
            await context.GetService<IMigrator>().MigrateAsync(
                cancellationToken: TestContext.Current.CancellationToken);
        }

        await ExecuteAsync(
            connectionString,
            $"DELETE FROM tenancy.tenant_modules WHERE tenant_id = '{LegacyTenantId}' AND module_key = 'customers';");

        await using var provider = TenancyServices(connectionString);
        await using var scope = provider.CreateAsyncScope();
        var modules = scope.ServiceProvider.GetRequiredService<ITenantModules>();

        Assert.Null(await modules.FindAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));
        var set = await modules.FindAsync(Guid.Parse(LegacyTenantId), TestContext.Current.CancellationToken);
        Assert.NotNull(set);
        Assert.False(set.IsContracted(TenantModuleKeys.Customers));
        Assert.False(set.IsEnabled(TenantModuleKeys.Quotations));
        Assert.True(set.IsEnabled(TenantModuleKeys.Catalog));
        Assert.Equal([TenantModuleKeys.Customers], set.MissingDependencies(TenantModuleKeys.Orders));
    }

    private static ServiceProvider TenancyServices(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QepDatabase"] = connectionString,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddTenancyInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private static TenancyDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "platform"))
            .Options);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<(string Key, string Source)>> RowsAsync(string connectionString, string tenantId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT module_key, source FROM tenancy.tenant_modules WHERE tenant_id = '{tenantId}' ORDER BY module_key",
            connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<(string, string)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
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
}
