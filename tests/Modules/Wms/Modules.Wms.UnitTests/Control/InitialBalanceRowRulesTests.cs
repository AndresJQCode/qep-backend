using Modules.Wms.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests.Control;

/// <summary>Las reglas puras de una fila de la carga inicial (RF-024): lo que decide si una fila entra, sin base ni DI.</summary>
public sealed class InitialBalanceRowRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly Warehouse Warehouse = WarehouseTests.NewWarehouse();

    private static readonly Location Bin = Warehouse.AddLocation(
        LocationId.New(), null, "A-01", LocationKind.Zone, LocationRole.Storage, true, true, LocationCapacity.None, 0, [], Now);

    private static readonly Location OtherBin = Warehouse.AddLocation(
        LocationId.New(), null, "A-02", LocationKind.Zone, LocationRole.Storage, true, true, LocationCapacity.None, 0, [], Now);

    private static WmsVariantSnapshot Variant(string code) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "Camiseta", "Camiseta – M", code, null, [12], true);

    private static LedgerVariantRules Rules(TrackingPolicy tracking = TrackingPolicy.None, int precision = 0) =>
        new("UND", precision, tracking, new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["CAJA"] = 12m });

    private static HashSet<(Guid VariantId, Guid LocationId, string? LotCode)> Seen() => [];

    private static InitialBalanceSheetRow Row(
        string? code = "ABC", string? location = "A-01", string? lot = null, string? expiration = null, string? quantity = "5", string? uom = null) =>
        new(2, code, location, lot, expiration, quantity, uom);

    private static InitialBalancePreviewRowDto Classify(
        InitialBalanceSheetRow row,
        IReadOnlyList<WmsVariantSnapshot> candidates,
        Location? location,
        LedgerVariantRules? rules,
        bool tracksExpiry = false,
        ISet<(Guid VariantId, Guid LocationId, string? LotCode)>? seen = null) =>
        InitialBalanceRowRules.Classify(row, candidates, location, rules, tracksExpiry, seen ?? Seen());

    [Fact]
    public void AnExactCodeWinsOverCaseInsensitiveCandidates()
    {
        var lower = Variant("abc");
        var exact = Variant("ABC");

        var result = Classify(Row("ABC"), [lower, exact], Bin, Rules());

        Assert.Equal(exact.VariantId, result.VariantId);
        Assert.Empty(result.Issues);
        Assert.Equal(5m, result.Quantity);
    }

    [Fact]
    public void TwoCaseInsensitiveCandidatesWithoutAnExactMatchAreAmbiguous()
    {
        var result = Classify(Row("Abc"), [Variant("abc"), Variant("ABC")], Bin, null);

        Assert.Equal([WmsErrorCodes.ImportAmbiguousCode], result.Issues);
        Assert.Null(result.VariantId);
    }

    [Fact]
    public void NoCandidateIsUnknown()
    {
        var result = Classify(Row("ZZZ"), [], Bin, null);

        Assert.Equal([WmsErrorCodes.ImportUnknownCode], result.Issues);
    }

    [Fact]
    public void ASingleInexactCandidateIsNeverAssignedByApproximation()
    {
        var result = Classify(Row("Abc"), [Variant("ABC")], Bin, null);

        Assert.Equal([WmsErrorCodes.ImportUnknownCode], result.Issues);
        Assert.Null(result.VariantId);
    }

    [Fact]
    public void ACajaUomIsConvertedToBaseWithTheProfileFactor()
    {
        var result = Classify(Row(quantity: "3", uom: "CAJA"), [Variant("ABC")], Bin, Rules());

        Assert.Empty(result.Issues);
        Assert.Equal(36m, result.Quantity);
        Assert.Equal("CAJA", result.Uom);
    }

    [Fact]
    public void AnUnknownUomIsAFactorMismatch()
    {
        var result = Classify(Row(uom: "PALET"), [Variant("ABC")], Bin, Rules());

        Assert.Equal([WmsErrorCodes.UomFactorMismatch], result.Issues);
    }

    [Fact]
    public void ARepeatedKeyIsADuplicateRowAndADifferentLocationIsNot()
    {
        var variant = Variant("ABC");
        var seen = Seen();

        var first = Classify(Row(), [variant], Bin, Rules(), seen: seen);
        var repeated = Classify(Row(), [variant], Bin, Rules(), seen: seen);
        var elsewhere = Classify(Row(location: "A-02"), [variant], OtherBin, Rules(), seen: seen);

        Assert.Empty(first.Issues);
        Assert.Equal([WmsErrorCodes.ImportDuplicateRow], repeated.Issues);
        Assert.Empty(elsewhere.Issues);
    }

    [Fact]
    public void AVirtualLocationIsUnknownForImport()
    {
        var result = Classify(Row(location: "ADJUSTMENT"), [Variant("ABC")], Warehouse.VirtualLocation(LocationRole.Adjustment), Rules());

        Assert.Equal([WmsErrorCodes.ImportUnknownLocation], result.Issues);
    }

    [Fact]
    public void AMissingLocationIsUnknown()
    {
        var result = Classify(Row(location: "ZZ-99"), [Variant("ABC")], null, Rules());

        Assert.Equal([WmsErrorCodes.ImportUnknownLocation], result.Issues);
        Assert.Null(result.LocationId);
    }

    [Theory]
    [InlineData(TrackingPolicy.Lot, null, WmsErrorCodes.LotRequired)]
    [InlineData(TrackingPolicy.None, "L-1", WmsErrorCodes.LotNotAllowed)]
    [InlineData(TrackingPolicy.Serial, null, WmsErrorCodes.SerialRequired)]
    public void TheLotRulesFollowTheTrackingPolicy(TrackingPolicy tracking, string? lot, string expected)
    {
        var result = Classify(Row(lot: lot), [Variant("ABC")], Bin, Rules(tracking));

        Assert.Equal([expected], result.Issues);
    }

    [Fact]
    public void ALotOnAnExpiringVariantNeedsAnExpirationDate()
    {
        var variant = Variant("ABC");

        var missing = Classify(Row(lot: "L-1"), [variant], Bin, Rules(TrackingPolicy.Lot), tracksExpiry: true);
        var given = Classify(Row(lot: "L-1", expiration: "2027-03-31"), [variant], Bin, Rules(TrackingPolicy.Lot), tracksExpiry: true);

        Assert.Equal([WmsErrorCodes.LotExpiryRequired], missing.Issues);
        Assert.Empty(given.Issues);
        Assert.Equal(new DateOnly(2027, 3, 31), given.ExpirationDate);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-4")]
    [InlineData(null)]
    public void ANonPositiveOrNonNumericQuantityIsInvalid(string? quantity)
    {
        var result = Classify(Row(quantity: quantity), [Variant("ABC")], Bin, Rules());

        Assert.Equal([WmsErrorCodes.MovementQuantityInvalid], result.Issues);
    }

    [Fact]
    public void MoreDecimalsThanThePrecisionExceedTheUom()
    {
        var result = Classify(Row(quantity: "2.5"), [Variant("ABC")], Bin, Rules(precision: 0));

        Assert.Equal([WmsErrorCodes.UomPrecisionExceeded], result.Issues);
    }
}
