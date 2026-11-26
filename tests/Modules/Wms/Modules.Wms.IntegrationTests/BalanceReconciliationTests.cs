using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Infrastructure;
using Modules.Wms.Infrastructure.Workers;
using Npgsql;
using Testcontainers.PostgreSql;
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

    // Dos pods concilian la misma bodega a la vez: sin el candado por bodega, los dos leen «hoy no hay fila» y la insertan dos veces.
    // No depende de ganar una carrera: un trigger propio de esta base deja cada INSERT esperando un candado que sostiene la prueba, así
    // que las dos pasadas quedan detenidas a la vez, antes de que alguna confirme, y recién ahí se sueltan.
    [Fact]
    public async Task TwoConcurrentReconciliationsRecordOneCheckPerDriftKey()
    {
        var token = TestContext.Current.CancellationToken;
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 10);
        await StockAsync(factory, s, s.Receiving, 4);
        await ExecuteAsync(database, "UPDATE wms.stock_balances SET on_hand = 7 WHERE location_id = @a", ("a", s.BinA));
        await ExecuteAsync(database, "UPDATE wms.stock_balances SET on_hand = 1 WHERE location_id = @r", ("r", s.Receiving));
        await ExecuteAsync(
            database,
            """
            CREATE FUNCTION wms.test_hold_balance_check() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_advisory_xact_lock_shared(990001); RETURN NEW; END $$;
            CREATE TRIGGER trg_test_hold_balance_check BEFORE INSERT ON wms.balance_checks
                FOR EACH ROW EXECUTE FUNCTION wms.test_hold_balance_check()
            """);
        var worker = factory.Services.GetRequiredService<BalanceReconciliationWorker>();
        await using var holder = new NpgsqlConnection(database.GetConnectionString());
        await holder.OpenAsync(token);
        await using var hold = await holder.BeginTransactionAsync(token);
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(990001)", holder, hold))
        {
            await take.ExecuteNonQueryAsync(token);
        }

        var first = Task.Run(() => worker.RunOnceAsync(token), token);
        var second = Task.Run(() => worker.RunOnceAsync(token), token);
        await WaitForAdvisoryWaitersAsync(database, 2);
        await hold.RollbackAsync(token);
        var drifts = await Task.WhenAll(first, second);

        // Las dos siguen reportando las dos diferencias, pero cada clave quedó registrada una sola vez.
        Assert.Equal([2L, 2L], drifts);
        Assert.Equal(0L, WmsGaugeReader.Read(factory, "qep.wms.reconciliation.failed"));
        Assert.Equal(2L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.balance_checks"));
        Assert.Equal(
            2L,
            await ScalarAsync<long>(database, "SELECT count(DISTINCT location_id) FROM wms.balance_checks"));
    }

    // Espera a que haya esa cantidad de sesiones de esta base detenidas en un advisory lock.
    private static async Task WaitForAdvisoryWaitersAsync(PostgreSqlContainer database, long expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        long waiting = 0;
        while (DateTime.UtcNow < deadline)
        {
            waiting = await ScalarAsync<long>(
                database,
                """
                SELECT count(*) FROM pg_locks
                WHERE locktype = 'advisory' AND NOT granted
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                """);
            if (waiting == expected)
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Expected {expected} sessions waiting on an advisory lock, found {waiting}.");
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

    // D-12: el ledger agrupa por la unidad de cada extremo y el JOIN la compara con IS NOT DISTINCT FROM; si no, el saldo suelto y el de
    // la unidad se sumarían en una sola clave del ledger y aparecerían dos diferencias que no existen.
    [Fact]
    public async Task TwoBalancesThatDifferOnlyInHandlingUnitDoNotDrift()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var unit = Guid.CreateVersion7();
        await StockAsync(factory, s, s.BinA, 10);
        var supplier = new StockKey(s.OwnerId, s.Supplier, s.VariantId, null, StockStatus.Available);
        var inUnit = new StockKey(s.OwnerId, s.BinA, s.VariantId, null, StockStatus.Available, unit);
        await SeedUnitMovementAsync(database, s, supplier, inUnit, 4, MovementReason.Adjustment);
        var worker = factory.Services.GetRequiredService<BalanceReconciliationWorker>();

        Assert.Equal(0L, await worker.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.balance_checks"));

        await ExecuteAsync(
            database, "UPDATE wms.stock_balances SET on_hand = 3 WHERE handling_unit_id = @unit", ("unit", unit));

        Assert.Equal(1L, await worker.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(unit, await ScalarAsync<Guid>(database, "SELECT handling_unit_id FROM wms.balance_checks"));
        Assert.Equal(4m, await ScalarAsync<decimal>(database, "SELECT ledger_on_hand FROM wms.balance_checks"));
        Assert.Equal(3m, await ScalarAsync<decimal>(database, "SELECT balance_on_hand FROM wms.balance_checks"));
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
