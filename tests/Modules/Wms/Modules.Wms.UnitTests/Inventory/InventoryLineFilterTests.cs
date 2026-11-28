using BuildingBlocks.Domain.Catalog;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests.Inventory;

/// <summary>Spec inventario básico §5.6: las líneas que no se pueden mover se apartan antes del ledger, cada una con su motivo.</summary>
public sealed class InventoryLineFilterTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private static InventoryEventLine Line(Guid? variant, decimal quantity = 1m, ProductKind? kind = null) =>
        new(Guid.CreateVersion7(), variant, quantity, kind);

    private static WmsVariantSnapshot Snapshot(Guid variant, ProductKind kind = ProductKind.Physical, SalesUnit unit = SalesUnit.Unit) =>
        new(variant, Guid.CreateVersion7(), "Producto", "Variante", "V-" + variant.ToString("N")[..6], null, [], true, kind, unit);

    private static VariantLogisticsProfile Profile(Guid variant, TrackingPolicy tracking = TrackingPolicy.None, int precision = 0) =>
        VariantLogisticsProfile.Create(
            Tenant,
            variant,
            Guid.CreateVersion7(),
            new ProfileChanges(UomCode.Parse("UND"), precision, tracking, false, null, null, null, null, null, null, null, []),
            Now);

    private static (IReadOnlyList<InventoryEventLine> Movable, IReadOnlyList<SkippedLine> Skipped) Split(
        IReadOnlyList<InventoryEventLine> lines,
        IEnumerable<WmsVariantSnapshot> snapshots,
        IEnumerable<VariantLogisticsProfile> profiles) =>
        InventoryLineFilter.Split(
            lines,
            snapshots.ToDictionary(snapshot => snapshot.VariantId),
            profiles.ToDictionary(profile => profile.VariantId));

    [Fact]
    public void APhysicalLineWithAKnownVariantMoves()
    {
        var variant = Guid.CreateVersion7();
        var line = Line(variant, 3m);

        var (movable, skipped) = Split([line], [Snapshot(variant)], []);

        Assert.Equal([line], movable);
        Assert.Empty(skipped);
    }

    [Fact]
    public void ALineWithoutVariantIsSkipped()
    {
        var line = Line(null);

        var (movable, skipped) = Split([line], [], []);

        Assert.Empty(movable);
        Assert.Equal(SkipReason.NoVariant, Assert.Single(skipped).Reason);
    }

    [Fact]
    public void AVariantNeitherInCatalogNorWithProfileIsSkipped()
    {
        var (movable, skipped) = Split([Line(Guid.CreateVersion7())], [], []);

        Assert.Empty(movable);
        Assert.Equal(SkipReason.UnknownVariant, Assert.Single(skipped).Reason);
    }

    [Fact]
    public void AVariantWithAProfileButUnknownToCatalogStillMoves()
    {
        var variant = Guid.CreateVersion7();

        var (movable, skipped) = Split([Line(variant)], [], [Profile(variant)]);

        Assert.Single(movable);
        Assert.Empty(skipped);
    }

    [Fact]
    public void AServiceLineByTheEventKindIsSkipped()
    {
        var variant = Guid.CreateVersion7();

        var (movable, skipped) = Split([Line(variant, kind: ProductKind.Service)], [Snapshot(variant)], []);

        Assert.Empty(movable);
        Assert.Equal(SkipReason.Service, Assert.Single(skipped).Reason);
    }

    [Fact]
    public void AServiceLineByTheCatalogKindIsSkippedWhenTheEventHasNoKind()
    {
        var variant = Guid.CreateVersion7();

        var (movable, skipped) = Split([Line(variant)], [Snapshot(variant, ProductKind.Service)], []);

        Assert.Empty(movable);
        Assert.Equal(SkipReason.Service, Assert.Single(skipped).Reason);
    }

    [Theory]
    [InlineData(TrackingPolicy.Lot)]
    [InlineData(TrackingPolicy.Serial)]
    public void ALotOrSerialTrackedVariantIsSkipped(TrackingPolicy tracking)
    {
        var variant = Guid.CreateVersion7();

        var (movable, skipped) = Split([Line(variant)], [Snapshot(variant)], [Profile(variant, tracking)]);

        Assert.Empty(movable);
        Assert.Equal(SkipReason.LotOrSerial, Assert.Single(skipped).Reason);
    }

    [Fact]
    public void AQuantityWithMoreDecimalsThanTheProfileAdmitsIsSkipped()
    {
        var variant = Guid.CreateVersion7();

        var (movable, skipped) = Split([Line(variant, 1.5m)], [Snapshot(variant)], [Profile(variant, precision: 0)]);

        Assert.Empty(movable);
        Assert.Equal(SkipReason.Precision, Assert.Single(skipped).Reason);
    }

    [Fact]
    public void WithoutProfileThePrecisionComesFromTheSalesUnit()
    {
        var kilos = Guid.CreateVersion7();
        var units = Guid.CreateVersion7();

        var (movable, skipped) = Split(
            [Line(kilos, 1.25m), Line(units, 1.25m)],
            [Snapshot(kilos, unit: SalesUnit.Kilogram), Snapshot(units)],
            []);

        Assert.Equal(kilos, Assert.Single(movable).VariantId);
        Assert.Equal(SkipReason.Precision, Assert.Single(skipped).Reason);
    }

    [Fact]
    public void TheRestOfTheLinesStillMoves()
    {
        var good = Guid.CreateVersion7();
        var service = Guid.CreateVersion7();
        var lines = new[] { Line(good), Line(service, kind: ProductKind.Service), Line(null) };

        var (movable, skipped) = Split(lines, [Snapshot(good), Snapshot(service)], []);

        Assert.Equal([lines[0]], movable);
        Assert.Equal(2, skipped.Count);
    }
}
