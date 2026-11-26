using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.UnitTests.Inventory;

public sealed class StockKeyComparerTests
{
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid LocationA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid LocationB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Variant = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid LotX = Guid.Parse("00000000-0000-0000-0000-000000000010");
    private static readonly Guid LotY = Guid.Parse("00000000-0000-0000-0000-000000000020");

    [Fact]
    public void NullLotSortsBeforeAnyLotAndTheOrderIsTotal()
    {
        StockKey[] keys =
        [
            new(Owner, LocationB, Variant, LotY, StockStatus.Available),
            new(Owner, LocationA, Variant, LotX, StockStatus.Received),
            new(Owner, LocationA, Variant, null, StockStatus.Available),
            new(Owner, LocationA, Variant, LotX, StockStatus.Available),
        ];
        var forward = keys.Order(StockKeyComparer.Instance).ToArray();
        var backward = keys.Reverse().Order(StockKeyComparer.Instance).ToArray();

        Assert.Equal(forward, backward);
        Assert.Equal(
            [(LocationA, (Guid?)null, StockStatus.Available), (LocationA, LotX, StockStatus.Available), (LocationA, LotX, StockStatus.Received), (LocationB, LotY, StockStatus.Available)],
            forward.Select(key => (key.LocationId, key.LotId, key.Status)).ToArray());
    }

    [Fact]
    public void NullHandlingUnitSortsBeforeAnyUnitAndTheOrderIsTotal()
    {
        var unit1 = Guid.Parse("00000000-0000-0000-0000-000000000100");
        var unit2 = Guid.Parse("00000000-0000-0000-0000-000000000200");
        StockKey[] keys =
        [
            new(Owner, LocationA, Variant, LotX, StockStatus.Available, unit2),
            new(Owner, LocationA, Variant, LotX, StockStatus.Received, unit1),
            new(Owner, LocationA, Variant, LotX, StockStatus.Received),
            new(Owner, LocationA, Variant, LotX, StockStatus.Available, unit1),
        ];
        var forward = keys.Order(StockKeyComparer.Instance).ToArray();
        var backward = keys.Reverse().Order(StockKeyComparer.Instance).ToArray();

        // La unidad va después del lote y antes del estado: la suelta (null) primero dentro de la misma (ubicación, variante, lote).
        Assert.Equal(forward, backward);
        Assert.Equal(
            [((Guid?)null, StockStatus.Received), (unit1, StockStatus.Available), (unit1, StockStatus.Received), (unit2, StockStatus.Available)],
            forward.Select(key => (key.HandlingUnitId, key.Status)).ToArray());
    }

    [Fact]
    public void KeysThatDifferOnlyInHandlingUnitAreDifferent()
    {
        var loose = new StockKey(Owner, LocationA, Variant, LotX, StockStatus.Available);
        var inUnit = loose with { HandlingUnitId = Guid.Parse("00000000-0000-0000-0000-000000000100") };

        Assert.NotEqual(0, StockKeyComparer.Instance.Compare(loose, inUnit));
        Assert.True(loose != inUnit);
    }

    [Fact]
    public void EqualKeysCompareAsZero() =>
        Assert.Equal(0, StockKeyComparer.Instance.Compare(
            new(Owner, LocationA, Variant, LotX, StockStatus.Blocked), new(Owner, LocationA, Variant, LotX, StockStatus.Blocked)));

    [Fact]
    public void TheInsufficientStockExceptionCarriesTheKeyAndTheAvailableQuantity()
    {
        var exception = new StockInsufficientException("A-01-02", Variant, "L-2609", 2m);
        Assert.Equal("wms.stock.insufficient", exception.Code);
        Assert.Equal(
            new Dictionary<string, object?> { ["locationCode"] = "A-01-02", ["variantId"] = Variant, ["lotCode"] = "L-2609", ["available"] = 2m },
            exception.ProblemExtensions);
    }

    [Fact]
    public void LedgerActorTypeMirrorsWmsActorType() =>
        Assert.Equal(Enum.GetNames<WmsActorType>(), Enum.GetNames<LedgerActorType>());
}
