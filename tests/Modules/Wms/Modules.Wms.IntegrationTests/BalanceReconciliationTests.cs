using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Infrastructure;
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
        // §6.5: la fila lleva el último movimiento visto de esa clave.
        Assert.Equal(
            await ScalarAsync<Guid>(
                database,
                "SELECT id FROM wms.stock_movements WHERE to_location_id = @a ORDER BY occurred_at DESC, id DESC LIMIT 1",
                ("a", s.BinA)),
            await ScalarAsync<Guid>(database, "SELECT last_movement_id FROM wms.balance_checks WHERE location_id = @a", ("a", s.BinA)));
        Assert.Equal(1L, WmsGaugeReader.Read(factory, "qep.wms.balance.drift"));
    }

    // Un reinicio dentro de la hora repite la pasada: no duplica la fila, y devuelve la diferencia (no las filas insertadas) para que el
    // gauge no se apague con la diferencia todavía ahí.
    [Fact]
    public async Task ARerunDoesNotDuplicateTheCheckAndStillReportsTheDrift()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 10);
        await ExecuteAsync(database, "UPDATE wms.stock_balances SET on_hand = 7 WHERE location_id = @a", ("a", s.BinA));
        var worker = factory.Services.GetRequiredService<BalanceReconciliationWorker>();

        Assert.Equal(1L, await worker.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1L, await worker.RunOnceAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.balance_checks"));
        Assert.Equal(1L, WmsGaugeReader.Read(factory, "qep.wms.balance.drift"));
    }

    [Fact]
    public async Task AFailedWarehouseDoesNotStopTheRestAndTheFailureIsReported()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        var failing = Guid.NewGuid();
        var reconciled = new List<Guid>();
        var warehouses = new[] { Guid.NewGuid(), failing, Guid.NewGuid() };
        services.AddScoped<IBalanceReconciliation>(_ => new FakeReconciliation(warehouses, failing, reconciled));
        await using var provider = services.BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        var worker = new BalanceReconciliationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new WmsMetrics(meterFactory),
            Options.Create(new WmsWorkerOptions()),
            NullLogger<BalanceReconciliationWorker>.Instance);

        var drift = await worker.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4L, drift);
        Assert.Equal([warehouses[0], warehouses[2]], reconciled);
        Assert.Equal(4L, WmsGaugeReader.Read(meterFactory, "qep.wms.balance.drift"));
        Assert.Equal(1L, WmsGaugeReader.Read(meterFactory, "qep.wms.reconciliation.failed"));
    }

    private sealed class FakeReconciliation(Guid[] warehouses, Guid failing, List<Guid> reconciled) : IBalanceReconciliation
    {
        public Task<IReadOnlyList<Guid>> ListWarehousesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>(warehouses);

        public Task<int> ReconcileWarehouseAsync(Guid warehouseId, CancellationToken cancellationToken)
        {
            if (warehouseId == failing)
            {
                throw new InvalidOperationException("boom");
            }

            reconciled.Add(warehouseId);
            return Task.FromResult(2);
        }
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
