using System.Text.RegularExpressions;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;
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

    // Review focus 1: the fingerprint saved by every period liquidated before vacations existed must still match, or all of them
    // would light up «sourceChanged». The literal was taken from the code before EngineInput gained Vacations.
    [Fact]
    public void NoVacationsKeepsTodaysFingerprint()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 1), 2_500_000m);

        Assert.Empty(input.Vacations);
        Assert.Equal("125cf020cd4c40255e43dd04984d08a9dc7c1f2eb8884a6f0dc46c5738d258d4", InputFingerprint.Of(input));
    }

    [Fact]
    public void AVacationChangesIt()
    {
        var input = EngineTestData.Input(Day);
        var withVacation = input with
        {
            Vacations = [EngineTestData.Vacation(new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 20), paidHere: true)],
        };

        Assert.NotEqual(InputFingerprint.Of(input), InputFingerprint.Of(withVacation));
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

    // Decision 4: a name is a label, not an input. Renaming a concept or a workplace never makes a period «sourceChanged».
    [Fact]
    public void RenamingAConceptOrAWorkplaceDoesNotChangeIt()
    {
        var conceptId = Guid.Parse("01900000-0000-7000-8000-0000000000f1");
        var recurringId = Guid.Parse("01900000-0000-7000-8000-0000000000f2");
        EngineInput With(string concept, string workplace) => EngineTestData.Input(Day) with
        {
            Workplace = new WorkplaceInfo(workplace, ArlClass.I, HighRiskPension: false),
            Overtime = [new OvertimeInput(conceptId, concept, 2m, null, 1.4m)],
            Incomes = [new IncomeInput(conceptId, concept, 100_000m, true)],
            Deductions = [new DeductionInput(conceptId, concept, 20_000m)],
            Recurring = [new RecurringInput(recurringId, concept, ConceptKind.Income, RecurringMode.Fixed, 50_000m, false, null)],
        };

        Assert.Equal(InputFingerprint.Of(With("Bono", "Sede principal")), InputFingerprint.Of(With("Bono de ventas", "Bodega norte")));
    }

    // Characterization: the nature of a concept still counts.
    [Fact]
    public void TheNatureOfAConceptStillChangesIt()
    {
        var input = EngineTestData.Input(Day) with
        {
            Incomes = [EngineTestData.Income(SystemConceptKey.OtherIncome, 100_000m, true)],
        };
        var notSalary = input with { Incomes = [input.Incomes[0] with { SalaryConstitutive = false }] };
        var otherConcept = input with { Incomes = [input.Incomes[0] with { ConceptId = Guid.NewGuid() }] };

        Assert.NotEqual(InputFingerprint.Of(input), InputFingerprint.Of(notSalary));
        Assert.NotEqual(InputFingerprint.Of(input), InputFingerprint.Of(otherConcept));
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
