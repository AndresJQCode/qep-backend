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

    private static InitialBalancePreviewRowDto WithContext(
        InitialBalanceSheetRow row, WmsVariantSnapshot variant, InitialBalanceRowContext context, Location? location = null) =>
        InitialBalanceRowRules.ClassifyDetailed(row, [variant], location ?? Bin, Rules(TrackingPolicy.Lot), false, Seen(), context).Row;

    // I-6: dos fechas para el mismo lote nuevo en la hoja, o una distinta de la de un lote que ya existe.
    [Fact]
    public void TheSameNewLotWithTwoExpiryDatesIsAConflict()
    {
        var variant = Variant("ABC");
        var dates = new Dictionary<(Guid VariantId, string LotCode), DateOnly?>();
        var context = new InitialBalanceRowContext(NewLotDates: dates);

        var first = WithContext(Row(lot: "L-1", expiration: "2027-01-31"), variant, context);
        var sameDate = WithContext(Row(lot: "L-1", expiration: "2027-01-31", location: "A-02"), variant, context, OtherBin);
        var otherDate = WithContext(Row(lot: "L-1", expiration: "2027-06-30", location: "A-02"), variant, context, OtherBin);

        Assert.Empty(first.Issues);
        Assert.Empty(sameDate.Issues);
        Assert.Equal([WmsErrorCodes.ImportLotExpiryConflict], otherDate.Issues);
    }

    [Fact]
    public void AnExistingLotWithAnotherExpiryIsAConflictAndWithTheSameOneIsNot()
    {
        var variant = Variant("ABC");
        var existing = Lot.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), variant.VariantId, "L-1", new DateOnly(2027, 1, 31), null, true, Now);
        var context = new InitialBalanceRowContext(ExistingLot: existing);

        var conflict = WithContext(Row(lot: "L-1", expiration: "2028-01-31"), variant, context);
        var same = WithContext(Row(lot: "L-1", expiration: "2027-01-31"), variant, context);
        var blank = WithContext(Row(lot: "L-1"), variant, context);

        Assert.Equal([WmsErrorCodes.ImportLotExpiryConflict], conflict.Issues);
        Assert.Empty(same.Issues);
        Assert.Empty(blank.Issues);
    }

    // I-7: ya hay saldo en esa clave.
    [Fact]
    public void AKeyThatAlreadyHasStockIsBlocked()
    {
        var result = InitialBalanceRowRules.ClassifyDetailed(
            Row(), [Variant("ABC")], Bin, Rules(), false, Seen(), new InitialBalanceRowContext(StockExists: true)).Row;

        Assert.Equal([WmsErrorCodes.ImportStockExists], result.Issues);
    }

    // M-1: una ubicación bloqueada o en conteo no admite la carga; mejor decirlo en el preview que en el commit.
    [Theory]
    [InlineData(LocationStatus.Blocked, WmsErrorCodes.LocationBlocked)]
    [InlineData(LocationStatus.Counting, WmsErrorCodes.LocationCounting)]
    public void ABlockedOrCountingLocationIsAnIssue(LocationStatus status, string expected)
    {
        var warehouse = WarehouseTests.NewWarehouse();
        var bin = warehouse.AddLocation(
            LocationId.New(), null, "B-01", LocationKind.Zone, LocationRole.Storage, true, true, LocationCapacity.None, 0, [], Now);
        warehouse.UpdateLocation(
            bin.Id, null, bin.Code, bin.Kind, bin.Role, bin.AllowsStorage, bin.AllowsPicking, bin.Capacity, bin.PutawayPriority, bin.StorageClasses, status, Now);

        var result = Classify(Row(location: "B-01"), [Variant("ABC")], bin, Rules());

        Assert.Equal([expected], result.Issues);
    }

    // M-2: más de lo que cabe en numeric(18,6), o una conversión que desborda, es una fila inválida y no un 500.
    [Theory]
    [InlineData("1000000000000")]
    [InlineData("79228162514264337593543950335")]
    public void AQuantityBeyondTheColumnIsInvalid(string quantity)
    {
        var result = Classify(Row(quantity: quantity, uom: "CAJA"), [Variant("ABC")], Bin, Rules());

        Assert.Contains(WmsErrorCodes.MovementQuantityInvalid, result.Issues);
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
