using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Wms.Infrastructure.Persistence;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

public sealed class InventoryLedgerMigrationTests
{
    // Sobre una base vacía y no sobre la de la fábrica: el Down de esta migración está vacío (S11), así que bajar y volver a subir
    // sobre una base ya migrada chocaría con las tablas que EF creó. Aquí sólo se sube: InitialWms, luego AddInventoryLedger.
    [Fact]
    public async Task TheLedgerMigrationCreatesTheTablesTheViewsAndTheQueue()
    {
        await using var database = await StartDatabaseAsync();
        var options = new DbContextOptionsBuilder<WmsDbContext>()
            .UseNpgsql(
                database.GetConnectionString(),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", WmsDbContext.Schema))
            .Options;
        await using var context = new WmsDbContext(options);
        var migrator = context.GetService<IMigrator>();
        var migrations = context.Database.GetMigrations().ToList();
        var initialWms = migrations.Single(m => m.EndsWith("_InitialWms", StringComparison.Ordinal));
        var addLedger = migrations.Single(m => m.EndsWith("_AddInventoryLedger", StringComparison.Ordinal));

        await migrator.MigrateAsync(initialWms, TestContext.Current.CancellationToken);
        await migrator.MigrateAsync(addLedger, TestContext.Current.CancellationToken);

        foreach (var relation in new[]
        {
            "wms.stock_movements", "wms.stock_balances", "wms.operation_receipts", "wms.adjustments", "wms.balance_change_queue",
            "wms.v_stock_rows", "wms.v_stock_summary", "wms.lots", "wms.serial_units", "wms.reservations", "wms.import_previews",
            "wms.balance_checks", "wms.adjustment_lines",
        })
        {
            Assert.NotNull(await ScalarAsync<string?>(database, "SELECT to_regclass(@name)::text", ("name", relation)));
        }

        Assert.Equal(1L, await ScalarAsync<long>(
            database,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'wms' AND table_name = 'stock_balances' AND column_name = 'allows_negative'"));
        Assert.Equal(0L, await ScalarAsync<long>(
            database,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'wms' AND column_name = 'location_is_virtual'"));
        Assert.Equal(1L, await ScalarAsync<long>(
            database,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'wms' AND table_name = 'stock_movements' AND column_name = 'reverses_occurred_at'"));

        await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);
    }
}
