using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Catalog.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Spec 2026-11-10 (impuestos por tipo), §11.1: toda tasa que ya existe pasa a ser IVA de Colombia
/// y su porcentaje entero sobrevive al ensanche a numeric(6,3). Type y country quedan sin default:
/// el dominio los exige en cada alta.
/// </summary>
public sealed class TaxRateTypeMigrationTests
{
    private const string MigrationSuffix = "_AddTaxRateTypeAndCountry";

    private const string TenantId = "01900000-0000-7000-8000-00000000e201";
    private const string TaxRateId = "01900000-0000-7000-8000-00000000e202";

    [Fact]
    public async Task ExistingRatesBecomeIvaColombia()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewCatalogContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        var migrations = context.Database.GetMigrations().ToList();
        var target = migrations.Single(id => id.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(target) - 1];

        await migrator.MigrateAsync(previous, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, $"""
            INSERT INTO catalog.tax_rates (id, tenant_id, name, percentage, is_active, version, created_at, updated_at)
            VALUES ('{TaxRateId}', '{TenantId}', 'IVA general', 19, true, 1,
                    '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z');
            """);

        await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("IVA|CO|19.000", await ScalarAsync<string>(connectionString,
            $"SELECT type || '|' || country || '|' || percentage::text FROM catalog.tax_rates WHERE id = '{TaxRateId}'"));
        Assert.True(await ScalarAsync<bool>(connectionString,
            $"SELECT minimum_base IS NULL FROM catalog.tax_rates WHERE id = '{TaxRateId}'"));
    }

    [Fact]
    public async Task TheTypeAndCountryColumnsHaveNoDefault()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewCatalogContext(connectionString);
        await context.GetService<IMigrator>().MigrateAsync(null, TestContext.Current.CancellationToken);

        var defaults = await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'catalog' AND table_name = 'tax_rates'
              AND column_name IN ('type', 'country') AND column_default IS NOT NULL
            """);
        var columns = await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'catalog' AND table_name = 'tax_rates'
              AND column_name IN ('type', 'country')
            """);

        Assert.Equal(2, columns);
        Assert.Equal(0, defaults);
    }

    private static CatalogDbContext NewCatalogContext(string connectionString) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "catalog"))
            .Options);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
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
