using BuildingBlocks.Domain;
using Modules.Wms.Domain.Inventory;
using Testcontainers.PostgreSql;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

/// <summary>Spec inventario básico §5.5: sólo una salida por venta (<c>Ship</c> con documento <c>Order</c> o <c>PosSale</c>) puede dejar
/// en negativo el saldo físico de origen, y nunca por debajo de lo asignado. Cualquier otro movimiento conserva el freno, también sobre
/// un saldo que ya esté negativo.</summary>
public sealed class SaleDebitTests
{
    private static readonly Guid SaleId = Guid.CreateVersion7();

    private static async Task<Guid> CustomerAsync(LedgerSetup s) =>
        (await ListLocationsAsync(s.Manager, s.WarehouseId)).Single(location => location.Code == "CUSTOMER").Id;

    private static LedgerEntry SaleEntry(LedgerSetup s, Guid customer, decimal quantity, string documentType = "PosSale") =>
        new(
            new StockKey(s.OwnerId, s.BinA, s.VariantId, null, StockStatus.Available),
            new StockKey(s.OwnerId, customer, s.VariantId, null, StockStatus.Available),
            quantity,
            null,
            MovementReason.Ship,
            new LedgerDocument(documentType, SaleId, Guid.CreateVersion7()),
            DateTimeOffset.UtcNow);

    private static Task<bool> AllowsNegativeAsync(PostgreSqlContainer database, LedgerSetup s) =>
        ScalarAsync<bool>(
            database,
            "SELECT allows_negative FROM wms.stock_balances WHERE location_id = @l AND variant_id = @v AND status = 'Available'",
            ("l", s.BinA),
            ("v", s.VariantId));

    [Fact]
    public async Task ASaleCanLeaveThePhysicalBalanceNegative()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var customer = await CustomerAsync(s);

        // Sin saldo previo (la variante nunca se recibió) y con él: las dos formas de quedar bajo cero.
        await PostAsync(factory, Batch(s, SaleEntry(s, customer, 3)));

        Assert.Equal(-3m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
        Assert.True(await AllowsNegativeAsync(database, s));

        await StockAsync(factory, s, s.BinA, 10);
        await PostAsync(factory, Batch(s, SaleEntry(s, customer, 12, "Order")));

        Assert.Equal(-5m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task ASaleThatKeepsTheBalancePositiveDoesNotRelaxIt()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var customer = await CustomerAsync(s);
        await StockAsync(factory, s, s.BinA, 10);

        await PostAsync(factory, Batch(s, SaleEntry(s, customer, 4)));

        Assert.Equal(6m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
        Assert.False(await AllowsNegativeAsync(database, s));
    }

    [Fact]
    public async Task AMoveFromANegativeBalanceFails()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var customer = await CustomerAsync(s);
        await PostAsync(factory, Batch(s, SaleEntry(s, customer, 3)));

        var move = Entry(s, s.BinA, StockStatus.Available, s.Receiving, StockStatus.Available, 1, MovementReason.InternalMove);
        var error = await Assert.ThrowsAsync<StockInsufficientException>(() => PostAsync(factory, Batch(s, move)));

        Assert.Equal("wms.stock.insufficient", error.Code);
        Assert.Equal(-3m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task APutawayFromANegativeBalanceFails()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var customer = await CustomerAsync(s);
        await PostAsync(factory, Batch(s, SaleEntry(s, customer, 3)));

        var putaway = Entry(s, s.BinA, StockStatus.Available, s.Receiving, StockStatus.Available, 1, MovementReason.Putaway);
        var error = await Assert.ThrowsAsync<StockInsufficientException>(() => PostAsync(factory, Batch(s, putaway)));

        Assert.Equal("wms.stock.insufficient", error.Code);
    }

    [Fact]
    public async Task ASaleCannotGoBelowAllocatedStock()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var customer = await CustomerAsync(s);
        await StockAsync(factory, s, s.BinA, 5);
        // Una asignación de T5: lo reservado vive en el saldo de la ubicación.
        await ExecuteAsync(
            database,
            "UPDATE wms.stock_balances SET reserved = 4 WHERE location_id = @l AND variant_id = @v AND status = 'Available'",
            ("l", s.BinA),
            ("v", s.VariantId));

        var error = await Assert.ThrowsAnyAsync<DomainException>(() => PostAsync(factory, Batch(s, SaleEntry(s, customer, 2))));

        Assert.Equal("wms.stock.reserved", error.Code);
        Assert.Equal(5m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));

        // Lo que sobra por encima de lo asignado sí se vende.
        await PostAsync(factory, Batch(s, SaleEntry(s, customer, 1)));
        Assert.Equal(4m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task AReversalOfASaleRaisesANegativeBalance()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var customer = await CustomerAsync(s);
        var sale = SaleEntry(s, customer, 3);
        var posting = await PostAsync(factory, Batch(s, sale));
        Assert.Equal(-3m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));

        var movementId = posting.MovementIds.Single();
        var reversal = LedgerEntry.Reverse(sale, movementId, sale.OccurredAt, DateTimeOffset.UtcNow, null);
        await PostAsync(factory, Batch(s, reversal));

        Assert.Equal(0m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task AShipThatIsNotASaleKeepsTheBrake()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var customer = await CustomerAsync(s);
        var plain = new LedgerEntry(
            new StockKey(s.OwnerId, s.BinA, s.VariantId, null, StockStatus.Available),
            new StockKey(s.OwnerId, customer, s.VariantId, null, StockStatus.Available),
            3,
            null,
            MovementReason.Ship,
            new LedgerDocument("Adjustment", SaleId, null),
            DateTimeOffset.UtcNow);

        var error = await Assert.ThrowsAsync<StockInsufficientException>(() => PostAsync(factory, Batch(s, plain)));

        Assert.Equal("wms.stock.insufficient", error.Code);
    }
}
