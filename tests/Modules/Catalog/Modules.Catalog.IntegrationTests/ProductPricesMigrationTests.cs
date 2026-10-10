using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Catalog.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Spec 2026-10-08, "Persistence (expand, deploy N)". Migrates Catalog alone up to the migration
/// before AddProductPrices with IMigrator, seeds products the way production has them (COP only,
/// USD only, both, a scale with stored finals and the two legacy history fields), then runs
/// AddProductPrices. Same mechanism as CustomerContactAddressMigrationTests.
/// </summary>
public sealed class ProductPricesMigrationTests
{
    private const string LastMigrationBeforeProductPrices = "20261001174931_MovePackagingUnitsToProduct";

    private const string TenantId = "01900000-0000-7000-8000-00000000c001";
    private const string BothId = "01900000-0000-7000-8000-00000000c002";
    private const string CopOnlyId = "01900000-0000-7000-8000-00000000c003";
    private const string UsdOnlyId = "01900000-0000-7000-8000-00000000c004";
    private const string ScaleId = "01900000-0000-7000-8000-00000000c005";
    private const string ChangeUsdId = "01900000-0000-7000-8000-00000000c006";
    private const string ChangeCopId = "01900000-0000-7000-8000-00000000c007";
    private const string ChangeScaleId = "01900000-0000-7000-8000-00000000c008";
    private const string AuthorId = "01900000-0000-7000-8000-00000000c009";
    private const string ZeroId = "01900000-0000-7000-8000-00000000c00a";
    private const string ChangeEurId = "01900000-0000-7000-8000-00000000c00b";

    private const string LegacySql = $"""
        INSERT INTO catalog.products (
            id, tenant_id, name, code, is_active, version, created_at, updated_at,
            price_base_usd, price_base_cop)
        VALUES
            ('{BothId}', '{TenantId}', 'Bronceador', '7416', true, 3,
             '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z', 9.97, 35900),
            ('{CopOnlyId}', '{TenantId}', 'Keratina', '7701', true, 1,
             '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z', NULL, 114700.50),
            ('{UsdOnlyId}', '{TenantId}', 'Kit', '7702', true, 1,
             '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z', 31.86, NULL),
            ('{ZeroId}', '{TenantId}', 'Muestra', '7703', true, 1,
             '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z', NULL, 0);
        INSERT INTO catalog.product_price_scales (
            id, product_id, tenant_id, from_unit, to_unit, discount, restriction, multiple,
            allow_grouping, final_usd, final_cop)
        VALUES ('{ScaleId}', '{BothId}', '{TenantId}', 6, 48, 15, 'Multiple', 6, false, 8.47, 30515);
        INSERT INTO catalog.product_price_changes (
            id, tenant_id, product_id, field, scale_from_unit, scale_to_unit,
            previous_value, new_value, changed_by, changed_at)
        VALUES
            ('{ChangeUsdId}', '{TenantId}', '{BothId}', 'PriceBaseUsd', NULL, NULL, 9.50, 9.97,
             '{AuthorId}', '2026-09-02T12:00:00Z'),
            ('{ChangeCopId}', '{TenantId}', '{BothId}', 'PriceBaseCop', NULL, NULL, 34000, 35900,
             '{AuthorId}', '2026-09-02T12:00:00Z'),
            ('{ChangeScaleId}', '{TenantId}', '{BothId}', 'ScaleDiscount', 6, 48, 10, 15,
             '{AuthorId}', '2026-09-02T12:00:00Z');
        """;

    [Fact]
    public async Task AddProductPricesCopiesEveryLegacyPriceLosslessly()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewCatalogContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeProductPrices, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacySql);

        await migrator.MigrateAsync(MigrationId(context, "_AddProductPrices"), TestContext.Current.CancellationToken);

        Assert.Equal(
            new[]
            {
                $"{BothId}|COP|35900.00",
                $"{BothId}|USD|9.97",
                $"{CopOnlyId}|COP|114700.50",
                $"{UsdOnlyId}|USD|31.86",
                // A zero price is a price (NOT NULL filter, not > 0): it must survive the copy.
                $"{ZeroId}|COP|0.00",
            },
            await RowsAsync(
                connectionString,
                "SELECT product_id::text || '|' || currency || '|' || amount::text " +
                "FROM catalog.product_prices ORDER BY product_id, currency"));
        // Expand, not move: the legacy columns are untouched for deploy N+1 and for rollback.
        Assert.Equal(35900m, await ScalarAsync<decimal>(
            connectionString, $"SELECT price_base_cop FROM catalog.products WHERE id = '{BothId}'"));
        Assert.Equal(30515m, await ScalarAsync<decimal>(
            connectionString, $"SELECT final_cop FROM catalog.product_price_scales WHERE id = '{ScaleId}'"));
    }

    [Fact]
    public async Task AddProductPricesRenamesTheHistoryToPriceBaseWithItsCurrency()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewCatalogContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeProductPrices, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacySql);

        await migrator.MigrateAsync(MigrationId(context, "_AddProductPrices"), TestContext.Current.CancellationToken);

        Assert.Equal(
            new[]
            {
                $"{ChangeUsdId}|PriceBase|USD",
                $"{ChangeCopId}|PriceBase|COP",
                $"{ChangeScaleId}|ScaleDiscount|",
            },
            await RowsAsync(
                connectionString,
                "SELECT id::text || '|' || field || '|' || coalesce(currency, '') " +
                "FROM catalog.product_price_changes ORDER BY id"));
    }

    // Spec says rollback is a redeploy; that is only true if the Down puts back what deploy N
    // wrote into product_prices (decision A7 of the plan).
    [Fact]
    public async Task RevertingCopiesTheCollectionBackIntoTheLegacyColumns()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewCatalogContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeProductPrices, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacySql);
        await migrator.MigrateAsync(MigrationId(context, "_AddProductPrices"), TestContext.Current.CancellationToken);
        // What deploy N can write: a new COP amount, an EUR price, a scale rewritten without finals.
        await ExecuteAsync(connectionString, $"""
            UPDATE catalog.product_prices SET amount = 36900 WHERE product_id = '{BothId}' AND currency = 'COP';
            INSERT INTO catalog.product_prices (product_id, currency, amount) VALUES ('{BothId}', 'EUR', 8.90);
            UPDATE catalog.product_price_scales SET final_cop = NULL, final_usd = NULL WHERE id = '{ScaleId}';
            INSERT INTO catalog.product_price_changes (
                id, tenant_id, product_id, field, scale_from_unit, scale_to_unit,
                previous_value, new_value, changed_by, changed_at, currency)
            VALUES ('{ChangeEurId}', '{TenantId}', '{BothId}', 'PriceBase', NULL, NULL, 8.50, 8.90,
                    '{AuthorId}', '2026-09-03T12:00:00Z', 'EUR');
            """);

        await migrator.MigrateAsync(LastMigrationBeforeProductPrices, TestContext.Current.CancellationToken);

        Assert.Equal(36900m, await ScalarAsync<decimal>(
            connectionString, $"SELECT price_base_cop FROM catalog.products WHERE id = '{BothId}'"));
        Assert.Equal(31365m, await ScalarAsync<decimal>(   // round(36900 × 0.85, 2)
            connectionString, $"SELECT final_cop FROM catalog.product_price_scales WHERE id = '{ScaleId}'"));
        Assert.Equal(8.47m, await ScalarAsync<decimal>(    // round(9.97 × 0.85, 2)
            connectionString, $"SELECT final_usd FROM catalog.product_price_scales WHERE id = '{ScaleId}'"));
        Assert.Equal("PriceBaseUsd", await ScalarAsync<string>(
            connectionString, $"SELECT field FROM catalog.product_price_changes WHERE id = '{ChangeUsdId}'"));
        // The old model cannot represent EUR history, so Down drops it instead of mislabelling it.
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, $"SELECT count(*) FROM catalog.product_price_changes WHERE id = '{ChangeEurId}'"));
        Assert.True(await ScalarAsync<bool>(
            connectionString, $"SELECT price_base_usd IS NULL FROM catalog.products WHERE id = '{CopOnlyId}'"));
        Assert.Equal("PriceBaseCop", await ScalarAsync<string>(
            connectionString, $"SELECT field FROM catalog.product_price_changes WHERE id = '{ChangeCopId}'"));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'catalog' AND table_name = 'product_prices'"));
    }

    private static CatalogDbContext NewCatalogContext(string connectionString) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "catalog"))
            .Options);

    /// <summary>The full id carries the generation timestamp; the suffix is what is stable.</summary>
    private static string MigrationId(CatalogDbContext context, string suffix) =>
        context.Database.GetMigrations().Single(id => id.EndsWith(suffix, StringComparison.Ordinal));

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

    private static async Task<string[]> RowsAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return [.. rows];
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
