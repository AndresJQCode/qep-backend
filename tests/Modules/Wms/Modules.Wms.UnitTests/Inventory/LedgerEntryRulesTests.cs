using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.UnitTests.Inventory;

public sealed class LedgerEntryRulesTests
{
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Variant = Guid.CreateVersion7();
    private static readonly Guid Lot = Guid.CreateVersion7();
    private static readonly Guid Warehouse = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly LedgerLocationInfo FromLocation = new(Guid.CreateVersion7(), Warehouse, "REC", false, false, false);
    private static readonly LedgerLocationInfo ToLocation = new(Guid.CreateVersion7(), Warehouse, "A-01", false, false, false);

    private static LedgerVariantRules Rules(
        int precision = 0,
        TrackingPolicy tracking = TrackingPolicy.None,
        IReadOnlyDictionary<string, decimal>? factors = null) =>
        new("UND", precision, tracking, factors ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase));

    private static LedgerEntry Entry(
        decimal quantity = 1m,
        Guid? lot = null,
        LedgerCapture? capture = null,
        MovementReason reason = MovementReason.InternalMove,
        DateTimeOffset? occurredAt = null,
        Guid? serialUnitId = null) =>
        new(
            new StockKey(Owner, FromLocation.Id, Variant, lot, StockStatus.Available),
            new StockKey(Owner, ToLocation.Id, Variant, lot, StockStatus.Available),
            quantity,
            capture,
            reason,
            null,
            occurredAt ?? Now,
            serialUnitId: serialUnitId);

    private static void Validate(LedgerEntry entry, LedgerVariantRules rules, LedgerLocationInfo? from = null, LedgerLocationInfo? to = null) =>
        LedgerEntryRules.Validate(entry, rules, from ?? FromLocation, to ?? ToLocation, Now);

    private static string CodeOf(Action action) => Assert.Throws<WmsDomainException>(action).Code;

    [Fact]
    public void AReversalDoesNotRevalidateTheCaptureOfTheOriginal()
    {
        // La captura es historia: el perfil pudo perder la conversión después. La cantidad base ya está en el movimiento.
        var original = Entry(24m, capture: new LedgerCapture("CAJA", 2m, 12m), reason: MovementReason.Adjustment);
        var reversal = LedgerEntry.Reverse(original, Guid.CreateVersion7(), Now.AddDays(-1), Now, null);

        Validate(reversal, Rules(precision: 0), ToLocation, FromLocation);

        Assert.Equal(original.Captured, reversal.Captured);
    }

    [Fact]
    public void AReversalSwapsTheHandlingUnitOfEachSide()
    {
        // La unidad puede cambiar entre extremos, como el estado: recibir suelto y almacenar dentro de un palet.
        var unit = Guid.CreateVersion7();
        var original = new LedgerEntry(
            new StockKey(Owner, FromLocation.Id, Variant, null, StockStatus.Received),
            new StockKey(Owner, ToLocation.Id, Variant, null, StockStatus.Available, unit),
            5m,
            null,
            MovementReason.Putaway,
            null,
            Now);

        var reversal = LedgerEntry.Reverse(original, Guid.CreateVersion7(), Now.AddDays(-1), Now, null);

        Assert.Equal(new StockKey(Owner, ToLocation.Id, Variant, null, StockStatus.Available, unit), reversal.From);
        Assert.Equal(new StockKey(Owner, FromLocation.Id, Variant, null, StockStatus.Received), reversal.To);
    }

    [Fact]
    public void ANewMovementWithAnUnknownCaptureIsStillRejected() =>
        Assert.Equal(
            "wms.uom.factor_mismatch",
            CodeOf(() => Validate(Entry(24m, capture: new LedgerCapture("CAJA", 2m, 12m)), Rules(precision: 0))));

    [Fact]
    public void ADecimalQuantityOnAZeroPrecisionProfileIsRejected() =>
        Assert.Equal("wms.uom.precision_exceeded", CodeOf(() => Validate(Entry(1.5m), Rules(precision: 0))));

    [Fact]
    public void AThreeDecimalQuantityOnASixPrecisionProfileIsAccepted() =>
        Validate(Entry(1.125m), Rules(precision: 6));

    [Fact]
    public void ALotTrackedVariantWithoutLotIsRejected() =>
        Assert.Equal("wms.lot.required", CodeOf(() => Validate(Entry(), Rules(tracking: TrackingPolicy.Lot))));

    [Fact]
    public void ALotTrackedVariantWithLotIsAccepted() =>
        Validate(Entry(lot: Lot), Rules(tracking: TrackingPolicy.Lot));

    [Fact]
    public void AnUntrackedVariantWithLotIsRejected() =>
        Assert.Equal("wms.lot.not_allowed", CodeOf(() => Validate(Entry(lot: Lot), Rules())));

    [Fact]
    public void ASerialMovementMustMoveExactlyOne()
    {
        var rules = Rules(tracking: TrackingPolicy.Serial);

        Assert.Equal("wms.serial.required", CodeOf(() => Validate(Entry(), rules)));
        Assert.Equal("wms.serial.required", CodeOf(() => Validate(Entry(2m, serialUnitId: Guid.CreateVersion7()), rules)));
        Validate(Entry(serialUnitId: Guid.CreateVersion7()), rules);
    }

    [Fact]
    public void ACaptureWhoseFactorDiffersFromTheProfileIsRejected()
    {
        var rules = Rules(factors: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["CAJA"] = 12m });

        Assert.Equal(
            "wms.uom.factor_mismatch",
            CodeOf(() => Validate(Entry(30m, capture: new LedgerCapture("CAJA", 3m, 10m)), rules)));
    }

    [Fact]
    public void ACaptureWhoseQuantityDoesNotMultiplyToTheBaseQuantityIsRejected()
    {
        var rules = Rules(factors: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["CAJA"] = 12m });

        Assert.Equal(
            "wms.uom.factor_mismatch",
            CodeOf(() => Validate(Entry(35m, capture: new LedgerCapture("CAJA", 3m, 12m)), rules)));
    }

    [Fact]
    public void ACaptureInTheBaseUomNeedsFactorOne()
    {
        Validate(Entry(5m, capture: new LedgerCapture("UND", 5m, 1m)), Rules());
        Assert.Equal("wms.uom.factor_mismatch", CodeOf(() => Validate(Entry(10m, capture: new LedgerCapture("UND", 5m, 2m)), Rules())));
    }

    [Fact]
    public void ACaptureInAnUnknownUomIsRejected() =>
        Assert.Equal(
            "wms.uom.factor_mismatch",
            CodeOf(() => Validate(Entry(12m, capture: new LedgerCapture("PALET", 1m, 12m)), Rules())));

    [Fact]
    public void ACaptureInADifferentCaseMatchesTheStoredConversion()
    {
        var rules = Rules(factors: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["EMPAQUE x12"] = 12m });

        Validate(Entry(12m, capture: new LedgerCapture("EMPAQUE X12", 1m, 12m)), rules);
    }

    [Fact]
    public void ABlockedLocationRejectsEveryReason()
    {
        var blocked = ToLocation with { IsBlocked = true };

        foreach (var reason in Enum.GetValues<MovementReason>())
        {
            Assert.Equal("wms.location.blocked", CodeOf(() => Validate(Entry(reason: reason), Rules(), to: blocked)));
        }
    }

    [Theory]
    [MemberData(nameof(AllReasons))]
    public void ACountingLocationAcceptsOnlyAdjustmentAndReversal(MovementReason reason)
    {
        var counting = FromLocation with { IsCounting = true };
        var entry = Entry(reason: reason);

        if (reason is MovementReason.Adjustment or MovementReason.Reversal)
        {
            Validate(entry, Rules(), from: counting);
        }
        else
        {
            Assert.Equal("wms.location.counting", CodeOf(() => Validate(entry, Rules(), from: counting)));
        }
    }

    public static TheoryData<MovementReason> AllReasons() => new(Enum.GetValues<MovementReason>());

    [Fact]
    public void AnOccurredAtOlderThanThirtyDaysIsRejected() =>
        Assert.Equal(
            "wms.movement.occurred_at_out_of_range",
            CodeOf(() => Validate(Entry(occurredAt: Now.AddDays(-30).AddSeconds(-1)), Rules())));

    [Fact]
    public void AnOccurredAtExactlyThirtyDaysOldIsAccepted() =>
        Validate(Entry(occurredAt: Now.AddDays(-30)), Rules());

    [Fact]
    public void AnOccurredAtWithinFiveMinutesAheadIsAcceptedAndBeyondIsNot()
    {
        Validate(Entry(occurredAt: Now.AddMinutes(5)), Rules());

        Assert.Equal(
            "wms.movement.occurred_at_out_of_range",
            CodeOf(() => Validate(Entry(occurredAt: Now.AddMinutes(5).AddSeconds(1)), Rules())));
    }

    [Fact]
    public void ALocationThatIsNotTheOneOfTheKeyIsAProgrammingError() =>
        Assert.Throws<ArgumentException>(() => Validate(Entry(), Rules(), from: ToLocation));
}
