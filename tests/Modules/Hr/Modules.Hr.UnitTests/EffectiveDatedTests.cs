using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

public sealed class EffectiveDatedTests
{
    private sealed record Entry(DateOnly EffectiveDate) : IEffectiveDated;

    private static readonly IReadOnlyList<Entry> Records =
    [
        new(new DateOnly(2026, 1, 15)),
        new(new DateOnly(2026, 6, 1)),
        new(new DateOnly(2026, 12, 1)),
    ];

    [Fact]
    public void TheLastRecordOnOrBeforeTodayIsCurrent() =>
        Assert.Equal(new DateOnly(2026, 6, 1), EffectiveDated.CurrentOf(Records, new DateOnly(2026, 11, 20)).EffectiveDate);

    [Fact]
    public void ARecordEffectiveTodayIsCurrent() =>
        Assert.Equal(new DateOnly(2026, 6, 1), EffectiveDated.CurrentOf(Records, new DateOnly(2026, 6, 1)).EffectiveDate);

    [Fact]
    public void AFutureRecordIsScheduled()
    {
        var today = new DateOnly(2026, 11, 20);

        Assert.True(EffectiveDated.IsScheduled(Records[2], today));
        Assert.False(EffectiveDated.IsScheduled(Records[1], today));
    }

    [Fact]
    public void BeforeTheContractStartsTheFirstRecordIsShown()
    {
        IReadOnlyList<Entry> only = [new(new DateOnly(2026, 12, 1))];

        Assert.Equal(new DateOnly(2026, 12, 1), EffectiveDated.CurrentOf(only, new DateOnly(2026, 11, 20)).EffectiveDate);
    }
}
