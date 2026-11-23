using Modules.Payroll.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.UnitTests;

public sealed class ArchivedLineQuotaTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Company = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly Guid Ana = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly Guid Luis = Guid.Parse("01900000-0000-7000-8000-0000000000e2");
    private static readonly Guid Bonus = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    private static readonly PayrollActor Actor = new(Guid.Parse("01900000-0000-7000-8000-0000000000a1"), "Ana Gómez");
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<PeriodInput> Saved()
    {
        var period = PayrollPeriod.Open(
            PayrollPeriodId.New(), Tenant, Company, (new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 15), PayrollHalf.First), Actor, Now);
        period.ReplaceInputs(Ana,
        [
            new PeriodInputDraft(Bonus, ConceptKind.Income, null, 100_000m, false),
            new PeriodInputDraft(Bonus, ConceptKind.Income, null, 50_000m, false),
        ], Actor, Now);
        return period.Inputs;
    }

    [Fact]
    public void TheLinesAlreadySavedCanBeTakenAgainButNotOneMore()
    {
        var quota = ArchivedLineQuota.Of(Saved(), Ana);

        Assert.True(quota.TryTake(Bonus, ConceptKind.Income));
        Assert.True(quota.TryTake(Bonus, ConceptKind.Income));
        Assert.False(quota.TryTake(Bonus, ConceptKind.Income));
    }

    [Fact]
    public void TheQuotaIsPerEmployeeAndKind()
    {
        Assert.False(ArchivedLineQuota.Of(Saved(), Luis).TryTake(Bonus, ConceptKind.Income));
        Assert.False(ArchivedLineQuota.Of(Saved(), Ana).TryTake(Bonus, ConceptKind.Deduction));
    }
}
