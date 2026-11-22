using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class OvertimeFactorsTests
{
    public static TheoryData<SystemConceptKey, double> NinetyPercent => new()
    {
        { SystemConceptKey.ExtraDay, 1.25 },
        { SystemConceptKey.ExtraNight, 1.75 },
        { SystemConceptKey.NightSurcharge, 0.35 },
        { SystemConceptKey.SundaySurcharge, 0.90 },
        { SystemConceptKey.ExtraSundayDay, 2.15 },
        { SystemConceptKey.ExtraSundayNight, 2.65 },
        { SystemConceptKey.NightSundaySurcharge, 1.25 },
    };

    public static TheoryData<SystemConceptKey, double> EightyPercent => new()
    {
        { SystemConceptKey.ExtraDay, 1.25 },
        { SystemConceptKey.ExtraNight, 1.75 },
        { SystemConceptKey.NightSurcharge, 0.35 },
        { SystemConceptKey.SundaySurcharge, 0.80 },
        { SystemConceptKey.ExtraSundayDay, 2.05 },
        { SystemConceptKey.ExtraSundayNight, 2.55 },
        { SystemConceptKey.NightSundaySurcharge, 1.15 },
    };

    [Theory]
    [MemberData(nameof(NinetyPercent))]
    public void SystemFactorsWithNinetyPercent(SystemConceptKey key, double expected) =>
        Assert.Equal((decimal)expected, OvertimeFactors.For(EngineTestData.Overtime(key, 1), EngineTestData.ForDate(new DateOnly(2026, 8, 1))).Factor);

    [Theory]
    [MemberData(nameof(EightyPercent))]
    public void SystemFactorsWithEightyPercent(SystemConceptKey key, double expected) =>
        Assert.Equal((decimal)expected, OvertimeFactors.For(EngineTestData.Overtime(key, 1), EngineTestData.ForDate(new DateOnly(2026, 6, 1))).Factor);

    [Fact]
    public void TheJulySecondHalfAlreadyUsesNinety() =>
        Assert.Equal(
            2.15m,
            OvertimeFactors.For(EngineTestData.Overtime(SystemConceptKey.ExtraSundayDay, 1), EngineTestData.ForDate(new DateOnly(2026, 7, 16))).Factor);

    [Fact]
    public void CompositesListTheKeysTheyAdded()
    {
        var (_, used) = OvertimeFactors.For(
            EngineTestData.Overtime(SystemConceptKey.ExtraSundayNight, 1),
            EngineTestData.ForDate(new DateOnly(2026, 8, 1)));

        Assert.Equal(["ExtraNightRate", "SundaySurchargeRate"], used.Select(value => value.Key));
    }

    [Fact]
    public void ACustomConceptUsesItsMultiplier()
    {
        var custom = new OvertimeInput(Guid.NewGuid(), "Hora de turno", 3m, null, 1.4m);

        var (factor, used) = OvertimeFactors.For(custom, EngineTestData.ForDate(new DateOnly(2026, 8, 1)));

        Assert.Equal(1.4m, factor);
        Assert.Empty(used);
    }

    [Fact]
    public void AnInputWithoutSystemKeyNorMultiplierIsRejected()
    {
        var invalid = new OvertimeInput(Guid.NewGuid(), "Nada", 1m, null, null);

        Assert.Throws<InvalidOperationException>(
            () => OvertimeFactors.For(invalid, EngineTestData.ForDate(new DateOnly(2026, 8, 1))));
    }
}
