using Modules.Hr.Domain;
using static Modules.Hr.UnitTests.HrTestData;

namespace Modules.Hr.UnitTests;

public sealed class FixedTermLimitsTests
{
    [Fact]
    public void TheCapIsFourYearsMinusADay()
    {
        Assert.Equal(new DateOnly(2030, 1, 14), FixedTermLimits.MaxEndDate(new DateOnly(2026, 1, 15)));
        Assert.Equal(new DateOnly(2028, 2, 28), FixedTermLimits.MaxEndDate(new DateOnly(2024, 2, 29)));
    }

    [Fact]
    public void ARenewalShorterThanAYearIsShort() =>
        Assert.True(FixedTermLimits.IsShort(new DateOnly(2026, 7, 14), new DateOnly(2027, 1, 14)));

    [Fact]
    public void ExactlyOneYearIsNotShort() =>
        Assert.False(FixedTermLimits.IsShort(new DateOnly(2028, 1, 14), new DateOnly(2029, 1, 14)));

    [Fact]
    public void NoticeIsDueWithinThirtyDays()
    {
        Assert.True(FixedTermLimits.NoticeDue(Hire(Contract(Terms(end: new DateOnly(2026, 12, 20)))).ActiveContract!, Today));
        Assert.False(FixedTermLimits.NoticeDue(Hire(Contract(Terms(end: new DateOnly(2026, 12, 21)))).ActiveContract!, Today));
    }

    [Fact]
    public void AnExpiredFixedTermIsStillDue()
    {
        var contract = Hire(Contract(Terms(end: new DateOnly(2026, 11, 1)))).ActiveContract!;

        Assert.True(FixedTermLimits.NoticeDue(contract, Today));
        Assert.Equal(-19, FixedTermLimits.DaysUntilEnd(contract, Today));
    }

    [Fact]
    public void IndefiniteOrTerminatedNeverDue()
    {
        var indefinite = Hire(Contract(Terms(duration: ContractDuration.Indefinite))).ActiveContract!;
        Assert.False(FixedTermLimits.NoticeDue(indefinite, Today));
        Assert.Null(FixedTermLimits.DaysUntilEnd(indefinite, Today));

        var employee = Hire(Contract(Terms(end: new DateOnly(2026, 12, 1))));
        var contract = employee.ActiveContract!;
        employee.Terminate(contract.Id, TerminationReason.Resignation, new DateOnly(2026, 11, 25), null, Now);
        Assert.False(FixedTermLimits.NoticeDue(contract, Today));
        Assert.Null(FixedTermLimits.DaysUntilEnd(contract, Today));
    }
}
