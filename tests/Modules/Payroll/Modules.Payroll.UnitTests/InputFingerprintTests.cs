using System.Text.RegularExpressions;
using Modules.Payroll.Application;
using Modules.Payroll.Domain.Engine;
using Modules.Payroll.UnitTests.Engine;

namespace Modules.Payroll.UnitTests;

/// <summary>La huella del <see cref="EngineInput"/> (R-16): decide si Hr, las capturas o la configuración cambiaron después de liquidar.</summary>
public sealed class InputFingerprintTests
{
    private static readonly DateOnly Day = new(2026, 11, 10);

    [Fact]
    public void TheSameInputHasTheSameFingerprint()
    {
        var first = InputFingerprint.Of(EngineTestData.Input(Day));
        var second = InputFingerprint.Of(EngineTestData.Input(Day));

        Assert.Equal(first, second);
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), first);
    }

    [Fact]
    public void TheLegalSnapshotDoesNotChangeIt()
    {
        var input = EngineTestData.Input(Day);
        var other = input with { Legal = EngineTestData.ForDate(new DateOnly(2026, 8, 1)) };

        Assert.Equal(InputFingerprint.Of(input), InputFingerprint.Of(other));
    }

    [Fact]
    public void ASalaryChangeChangesIt()
    {
        var input = EngineTestData.Input(Day);
        var raised = input with
        {
            Salaries = [new SalarySegmentSource(new DateOnly(2025, 1, 1), 2_000_000m, false)],
        };

        Assert.NotEqual(InputFingerprint.Of(input), InputFingerprint.Of(raised));
    }

    [Fact]
    public void ACaptureOrASettingChangesIt()
    {
        var input = EngineTestData.Input(Day);

        var captured = input with { Overtime = [EngineTestData.Overtime(Modules.Payroll.Domain.SystemConceptKey.ExtraDay, 2m)] };
        var settings = input with { Settings = input.Settings with { Exonerated = false } };

        Assert.NotEqual(InputFingerprint.Of(input), InputFingerprint.Of(captured));
        Assert.NotEqual(InputFingerprint.Of(input), InputFingerprint.Of(settings));
    }

    [Fact]
    public void ATrailingZeroInADecimalDoesNotChangeIt()
    {
        var input = EngineTestData.Input(Day);
        var scaled = input with { Salaries = [new SalarySegmentSource(new DateOnly(2025, 1, 1), 1_750_905.00m, false)] };

        Assert.Equal(InputFingerprint.Of(input), InputFingerprint.Of(scaled));
    }

    [Fact]
    public void ThePeriodFingerprintIgnoresOrder()
    {
        var a = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var b = Guid.Parse("00000000-0000-0000-0000-00000000000b");
        var c = Guid.Parse("00000000-0000-0000-0000-00000000000c");

        var one = InputFingerprint.OfPeriod([(a, "fa"), (b, "fb")], [(c, "SalaryBelowMinimum")]);
        var two = InputFingerprint.OfPeriod([(b, "fb"), (a, "fa")], [(c, "SalaryBelowMinimum")]);

        Assert.Equal(one, two);
        Assert.NotEqual(one, InputFingerprint.OfPeriod([(a, "fa"), (b, "fX")], [(c, "SalaryBelowMinimum")]));
        Assert.NotEqual(one, InputFingerprint.OfPeriod([(a, "fa"), (b, "fb")], [(c, "MissingWorkplace")]));
        Assert.NotEqual(one, InputFingerprint.OfPeriod([(a, "fa"), (b, "fb")], []));
    }
}
