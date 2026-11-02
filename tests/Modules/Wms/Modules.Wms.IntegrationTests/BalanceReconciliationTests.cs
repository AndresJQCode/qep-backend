using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Infrastructure.Workers;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

public sealed class BalanceReconciliationTests
{
    [Fact]
    public async Task ASeededDriftIsRecordedAndNotCorrected()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 10);
        await ExecuteAsync(database, "UPDATE wms.stock_balances SET on_hand = 7 WHERE location_id = @a", ("a", s.BinA));
        var worker = factory.Services.GetRequiredService<BalanceReconciliationWorker>();

        var drift = await worker.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1L, drift);
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.balance_checks"));
        Assert.Equal(
            10m,
            await ScalarAsync<decimal>(database, "SELECT ledger_on_hand FROM wms.balance_checks WHERE location_id = @a", ("a", s.BinA)));
        Assert.Equal(
            7m,
            await ScalarAsync<decimal>(database, "SELECT balance_on_hand FROM wms.balance_checks WHERE location_id = @a", ("a", s.BinA)));
        Assert.Equal(7m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
        Assert.Equal(1L, WmsGaugeReader.Read(factory, "qep.wms.balance.drift"));
    }

    [Fact]
    public async Task AConsistentWarehouseWritesNothing()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 10);
        await StockAsync(factory, s, s.Receiving, 4);
        // Un cambio de estado y una devolución al proveedor: el ledger y los saldos tienen que seguir cuadrando.
        await PostAsync(factory, Batch(s, Entry(s, s.BinA, StockStatus.Available, s.BinA, StockStatus.Blocked, 3)));
        await PostAsync(factory, Batch(s, Entry(s, s.Receiving, StockStatus.Available, s.Supplier, StockStatus.Available, 4)));
        var worker = factory.Services.GetRequiredService<BalanceReconciliationWorker>();

        var drift = await worker.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0L, drift);
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.balance_checks"));
        Assert.Equal(0L, WmsGaugeReader.Read(factory, "qep.wms.balance.drift"));
    }

    [Fact]
    public async Task ABalanceWhoseVariantTheLedgerDoesNotKnowIsDriftToo()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 2);
        await ExecuteAsync(
            database,
            "UPDATE wms.stock_balances SET variant_id = @other WHERE location_id = @a AND status = 'Available'",
            ("other", Guid.CreateVersion7()),
            ("a", s.BinA));
        var worker = factory.Services.GetRequiredService<BalanceReconciliationWorker>();

        // Dos claves: la variante que el ledger conoce y el saldo ya no tiene, y la que el saldo tiene y el ledger no conoce.
        Assert.Equal(2L, await worker.RunOnceAsync(TestContext.Current.CancellationToken));
    }
}
