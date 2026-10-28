using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Catalog.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Spec 2026-10-08 (multi-currency), "Persistence (contract, deploy N+1)": the legacy columns go
/// only after proving the collection holds everything they held. A drift aborts the whole migration
/// (RAISE EXCEPTION, SqlState P0001) and leaves the columns in place.
///
/// Against the variant schema (spec 2026-10-20): the legacy product-level price belongs to the
/// default variant, the one that carries the product's id, so the guard reads product_prices and
/// the PriceBase history by <c>variant_id = product id</c>, and a scale final is checked against
/// the price of its own variant. The tests seed at the migration right before the contract one,
/// which is the shape production has when it runs.
/// </summary>
public sealed class LegacyProductPriceColumnsMigrationTests
{
    private const string DropMigrationSuffix = "_DropLegacyProductPriceColumns";

    private const string TenantId = "01900000-0000-7000-8000-00000000d101";
    private const string ProductId = "01900000-0000-7000-8000-00000000d102";
    private const string ScaleId = "01900000-0000-7000-8000-00000000d103";
    private const string ChangeId = "01900000-0000-7000-8000-00000000d104";
    private const string AuthorId = "01900000-0000-7000-8000-00000000d105";
    private const string LaterChangeId = "01900000-0000-7000-8000-00000000d106";
    private const string RedVariantId = "01900000-0000-7000-8000-00000000d107";
    private const string RedScaleId = "01900000-0000-7000-8000-00000000d108";
    private const string RedChangeId = "01900000-0000-7000-8000-00000000d109";

    private const string LegacyColumnsCountSql = """
        SELECT count(*) FROM information_schema.columns
        WHERE table_schema = 'catalog'
          AND ((table_name = 'products' AND column_name IN ('price_base_cop', 'price_base_usd'))
            OR (table_name = 'product_price_scales' AND column_name IN ('final_cop', 'final_usd')))
        """;

    // A product from before deploy N: its legacy columns still hold the prices AddProductPrices
    // copied, its default variant (same id, AddProductVariants) holds the product_prices row and
    // the scale with its legacy final.
    private static string ProductSql(decimal? legacyCop, decimal? legacyUsd, decimal? finalCop, bool copRow) => $"""
        INSERT INTO catalog.products (id, tenant_id, name, is_active, version, created_at, updated_at,
                                      price_base_cop, price_base_usd)
        VALUES ('{ProductId}', '{TenantId}', 'Vela de soja', true, 1,
                '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z',
                {Sql(legacyCop)}, {Sql(legacyUsd)});
        INSERT INTO catalog.product_variants (id, product_id, tenant_id, code, barcode, combination_key,
                                              packaging_units, gallery_image_id, is_active, position)
        VALUES ('{ProductId}', '{ProductId}', '{TenantId}', 'VS-001', NULL, '', ARRAY[]::integer[], NULL, true, 0);
        {(copRow ? $"INSERT INTO catalog.product_prices (variant_id, currency, amount) VALUES ('{ProductId}', 'COP', 45000);" : "")}
        INSERT INTO catalog.product_price_scales (id, variant_id, tenant_id, from_unit, to_unit, discount,
                                                  restriction, multiple, allow_grouping, final_cop, final_usd)
        VALUES ('{ScaleId}', '{ProductId}', '{TenantId}', 1, 9, 10, 'Multiple', 1, false, {Sql(finalCop)}, NULL);
        """;

    // A second variant of the same product, added after the variants deploy: its own id, its own
    // price and its own scale. The legacy columns never described it.
    private static string RedVariantSql(string currency, decimal amount, decimal? scaleFinalCop) => $"""
        INSERT INTO catalog.product_variants (id, product_id, tenant_id, code, barcode, combination_key,
                                              packaging_units, gallery_image_id, is_active, position)
        VALUES ('{RedVariantId}', '{ProductId}', '{TenantId}', 'VS-001-R', NULL, 'r', ARRAY[]::integer[], NULL, true, 1);
        INSERT INTO catalog.product_prices (variant_id, currency, amount)
        VALUES ('{RedVariantId}', '{currency}', {Sql(amount)});
        INSERT INTO catalog.product_price_scales (id, variant_id, tenant_id, from_unit, to_unit, discount,
                                                  restriction, multiple, allow_grouping, final_cop, final_usd)
        VALUES ('{RedScaleId}', '{RedVariantId}', '{TenantId}', 1, 9, 10, 'Multiple', 1, false, {Sql(scaleFinalCop)}, NULL);
        """;

    private static string PriceBaseChangeSql(
        string id, string variantId, string currency, decimal? previous, decimal? next, string changedAt) => $"""
        INSERT INTO catalog.product_price_changes (id, tenant_id, product_id, variant_id, field, currency,
            previous_value, new_value, changed_by, changed_at)
        VALUES ('{id}', '{TenantId}', '{ProductId}', '{variantId}', 'PriceBase', '{currency}',
                {Sql(previous)}, {Sql(next)}, '{AuthorId}', '{changedAt}');
        """;

    private static string Sql(decimal? value) =>
        value is { } number ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL";

    // The red variant's scale final matches FinalFor(50000, 10) = 45000, its own price; checked
    // against the default variant's 45000 it would be off by 4500 and abort.
    [Fact]
    public async Task AConsistentCatalogueLosesTheLegacyColumns()
    {
        await using var fixture = await MigratedToBeforeTheContractAsync();
        await ExecuteAsync(fixture.ConnectionString,
            ProductSql(legacyCop: 45_000m, legacyUsd: null, finalCop: 40_500m, copRow: true)
            + RedVariantSql("COP", 50_000m, scaleFinalCop: 45_000m));

        await fixture.MigrateToContractAsync();

        Assert.Equal(0L, await ScalarAsync<long>(fixture.ConnectionString, LegacyColumnsCountSql));
    }

    // The common production shape: the price was edited during deploy N, so the legacy column is
    // stale and the scale was rewritten without finals. The guard checks that a row exists, not
    // that the amounts agree: product_prices is the truth from deploy N on.
    [Fact]
    public async Task APriceEditedDuringDeployNDoesNotBlockTheDrop()
    {
        await using var fixture = await MigratedToBeforeTheContractAsync();
        await ExecuteAsync(fixture.ConnectionString,
            ProductSql(legacyCop: 45_000m, legacyUsd: null, finalCop: null, copRow: true) + $"""
            UPDATE catalog.product_prices SET amount = 50000 WHERE variant_id = '{ProductId}' AND currency = 'COP';
            """);

        await fixture.MigrateToContractAsync();

        Assert.Equal(0L, await ScalarAsync<long>(fixture.ConnectionString, LegacyColumnsCountSql));
    }

    // FinalFor(45000, 10) = 40500; 40000 is off by 500, far beyond the one-cent tolerance the
    // legacy validator allowed (plan decision A9).
    [Fact]
    public async Task AScaleFinalThatDriftedAbortsAndKeepsTheColumns()
    {
        await using var fixture = await MigratedToBeforeTheContractAsync();
        await ExecuteAsync(fixture.ConnectionString,
            ProductSql(legacyCop: 45_000m, legacyUsd: null, finalCop: 40_000m, copRow: true));

        var exception = await Assert.ThrowsAsync<PostgresException>(fixture.MigrateToContractAsync);

        Assert.Equal("P0001", exception.SqlState);
        Assert.Contains("FinalFor", exception.MessageText, StringComparison.Ordinal);
        Assert.Contains(ScaleId, exception.MessageText, StringComparison.Ordinal);
        Assert.Equal(4L, await ScalarAsync<long>(fixture.ConnectionString, LegacyColumnsCountSql));
    }

    // A9 tolerance: a stored final one cent away from FinalFor is legacy rounding, not drift.
    [Fact]
    public async Task AScaleFinalWithinOneCentDoesNotBlockTheDrop()
    {
        await using var fixture = await MigratedToBeforeTheContractAsync();
        await ExecuteAsync(fixture.ConnectionString,
            ProductSql(legacyCop: 45_000m, legacyUsd: null, finalCop: 40_500.01m, copRow: true));

        await fixture.MigrateToContractAsync();

        Assert.Equal(0L, await ScalarAsync<long>(fixture.ConnectionString, LegacyColumnsCountSql));
    }

    // The message names the default variant's code (products has no code since the variants) and
    // the product id, so whoever runs the deploy knows which row to fix.
    [Fact]
    public async Task ALegacyPriceWithoutItsRowAborts()
    {
        await using var fixture = await MigratedToBeforeTheContractAsync();
        await ExecuteAsync(fixture.ConnectionString,
            ProductSql(legacyCop: 45_000m, legacyUsd: 12m, finalCop: 40_500m, copRow: true));

        var exception = await Assert.ThrowsAsync<PostgresException>(fixture.MigrateToContractAsync);

        Assert.Equal("P0001", exception.SqlState);
        Assert.Contains("VS-001", exception.MessageText, StringComparison.Ordinal);
        Assert.Contains(ProductId, exception.MessageText, StringComparison.Ordinal);
        Assert.Contains("USD", exception.MessageText, StringComparison.Ordinal);
        Assert.Equal(4L, await ScalarAsync<long>(fixture.ConnectionString, LegacyColumnsCountSql));
    }

    // Removing the USD price through the API during deploy N leaves price_base_usd stale and a
    // PriceBase/USD history row whose new value is null: that is not drift (plan decision A8).
    [Fact]
    public async Task APriceRemovedThroughTheApiDoesNotBlockTheDrop()
    {
        await using var fixture = await MigratedToBeforeTheContractAsync();
        await ExecuteAsync(fixture.ConnectionString,
            ProductSql(legacyCop: 45_000m, legacyUsd: 12m, finalCop: 40_500m, copRow: true)
            + PriceBaseChangeSql(ChangeId, ProductId, "USD", 12m, null, "2026-10-09T12:00:00Z"));

        await fixture.MigrateToContractAsync();

        Assert.Equal(0L, await ScalarAsync<long>(fixture.ConnectionString, LegacyColumnsCountSql));
    }

    // A8 reads the latest history row; two rows in the same instant are ordered by id (GUID v7,
    // the later write has the higher id). Here the removal is the later one: no drift.
    [Fact]
    public async Task AnA8TieIsBrokenByIdWhenTheRemovalIsLater()
    {
        await using var fixture = await MigratedToBeforeTheContractAsync();
        await ExecuteAsync(fixture.ConnectionString,
            ProductSql(legacyCop: 45_000m, legacyUsd: 12m, finalCop: 40_500m, copRow: true)
            + PriceBaseChangeSql(ChangeId, ProductId, "USD", 10m, 12m, "2026-10-09T12:00:00Z")
            + PriceBaseChangeSql(LaterChangeId, ProductId, "USD", 12m, null, "2026-10-09T12:00:00Z"));

        await fixture.MigrateToContractAsync();

        Assert.Equal(0L, await ScalarAsync<long>(fixture.ConnectionString, LegacyColumnsCountSql));
    }

    // Same instant, but the removal is the earlier id and a later write set the price again: the
    // latest row is not a removal, the row is missing, and that is drift.
    [Fact]
    public async Task AnA8TieIsBrokenByIdWhenTheRemovalIsEarlier()
    {
        await using var fixture = await MigratedToBeforeTheContractAsync();
        await ExecuteAsync(fixture.ConnectionString,
            ProductSql(legacyCop: 45_000m, legacyUsd: 12m, finalCop: 40_500m, copRow: true)
            + PriceBaseChangeSql(ChangeId, ProductId, "USD", 12m, null, "2026-10-09T12:00:00Z")
            + PriceBaseChangeSql(LaterChangeId, ProductId, "USD", null, 12m, "2026-10-09T12:00:00Z"));

        var exception = await Assert.ThrowsAsync<PostgresException>(fixture.MigrateToContractAsync);

        Assert.Equal("P0001", exception.SqlState);
        Assert.Contains("VS-001", exception.MessageText, StringComparison.Ordinal);
        Assert.Equal(4L, await ScalarAsync<long>(fixture.ConnectionString, LegacyColumnsCountSql));
    }

    // The legacy USD price belongs to the default variant. Another variant of the same product with
    // a USD row, and a later USD removal recorded on that other variant, say nothing about it: the
    // default variant has no USD row and no removal of its own, so the guard aborts.
    [Fact]
    public async Task AnotherVariantDoesNotCoverTheDefaultVariantsMissingPrice()
    {
        await using var fixture = await MigratedToBeforeTheContractAsync();
        await ExecuteAsync(fixture.ConnectionString,
            ProductSql(legacyCop: 45_000m, legacyUsd: 12m, finalCop: 40_500m, copRow: true)
            + RedVariantSql("USD", 13m, scaleFinalCop: null)
            + PriceBaseChangeSql(RedChangeId, RedVariantId, "USD", 13m, null, "2026-10-22T12:00:00Z")
            + $"INSERT INTO catalog.product_prices (variant_id, currency, amount) VALUES ('{RedVariantId}', 'COP', 50000);");

        var exception = await Assert.ThrowsAsync<PostgresException>(fixture.MigrateToContractAsync);

        Assert.Equal("P0001", exception.SqlState);
        Assert.Contains("VS-001 (", exception.MessageText, StringComparison.Ordinal);
        Assert.DoesNotContain("VS-001-R", exception.MessageText, StringComparison.Ordinal);
        Assert.Equal(4L, await ScalarAsync<long>(fixture.ConnectionString, LegacyColumnsCountSql));
    }

    // Deleting the default variant through the API (deletedVariantIds) cascades its prices and its
    // history, so A8 has no row to read. AddProductVariants created that variant for every product,
    // so its absence proves the removal: the legacy price is stale, not drift (A8's rationale).
    [Fact]
    public async Task ADeletedDefaultVariantDoesNotBlockTheDrop()
    {
        await using var fixture = await MigratedToBeforeTheContractAsync();
        await ExecuteAsync(fixture.ConnectionString,
            ProductSql(legacyCop: 45_000m, legacyUsd: 12m, finalCop: 40_500m, copRow: true)
            + RedVariantSql("COP", 50_000m, scaleFinalCop: null)
            + $"DELETE FROM catalog.product_variants WHERE id = '{ProductId}';");

        await fixture.MigrateToContractAsync();

        Assert.Equal(0L, await ScalarAsync<long>(fixture.ConnectionString, LegacyColumnsCountSql));
    }

    // Stepping back must leave the columns the earlier migrations' Down expect: they return, the
    // product price filled from its default variant, every scale final recomputed as FinalFor from
    // its own variant's price. Migrating forward again drops them again.
    [Fact]
    public async Task RevertingPutsTheColumnsBackFilledFromTheCollection()
    {
        await using var fixture = await MigratedToBeforeTheContractAsync();
        await ExecuteAsync(fixture.ConnectionString,
            ProductSql(legacyCop: 45_000m, legacyUsd: null, finalCop: 40_500m, copRow: true)
            + RedVariantSql("COP", 50_000m, scaleFinalCop: null));
        await fixture.MigrateToContractAsync();

        await fixture.Migrator.MigrateAsync(fixture.PreviousMigration, TestContext.Current.CancellationToken);

        Assert.Equal(4L, await ScalarAsync<long>(fixture.ConnectionString, LegacyColumnsCountSql));
        Assert.Equal(45_000m, await ScalarAsync<decimal>(
            fixture.ConnectionString, $"SELECT price_base_cop FROM catalog.products WHERE id = '{ProductId}'"));
        Assert.True(await ScalarAsync<bool>(
            fixture.ConnectionString, $"SELECT price_base_usd IS NULL FROM catalog.products WHERE id = '{ProductId}'"));
        Assert.Equal(40_500m, await ScalarAsync<decimal>(
            fixture.ConnectionString, $"SELECT final_cop FROM catalog.product_price_scales WHERE id = '{ScaleId}'"));
        Assert.True(await ScalarAsync<bool>(
            fixture.ConnectionString, $"SELECT final_usd IS NULL FROM catalog.product_price_scales WHERE id = '{ScaleId}'"));
        Assert.Equal(45_000m, await ScalarAsync<decimal>(
            fixture.ConnectionString, $"SELECT final_cop FROM catalog.product_price_scales WHERE id = '{RedScaleId}'"));

        await fixture.Migrator.MigrateAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(0L, await ScalarAsync<long>(fixture.ConnectionString, LegacyColumnsCountSql));
    }

    private static async Task<MigrationFixture> MigratedToBeforeTheContractAsync()
    {
        var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var context = NewCatalogContext(connectionString);
        var migrator = context.GetService<IMigrator>();

        var migrations = context.Database.GetMigrations().ToList();
        var contract = migrations.Single(id => id.EndsWith(DropMigrationSuffix, StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(contract) - 1];

        await migrator.MigrateAsync(previous, TestContext.Current.CancellationToken);
        return new MigrationFixture(database, context, migrator, connectionString, previous, contract);
    }

    private sealed record MigrationFixture(
        PostgreSqlContainer Database,
        CatalogDbContext Context,
        IMigrator Migrator,
        string ConnectionString,
        string PreviousMigration,
        string ContractMigration) : IAsyncDisposable
    {
        public Task MigrateToContractAsync() =>
            Migrator.MigrateAsync(ContractMigration, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    // NewCatalogContext, ExecuteAsync, ScalarAsync, StartDatabaseAsync: copied from
    // ProductPricesMigrationTests (migrations are frozen history; the tests stay independent).
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
