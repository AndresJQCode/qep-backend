using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.UnitTests;

/// <summary>El IPC de un año para un tenant (spec reajuste §6.7).</summary>
public sealed class IpcRateTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheSameRateChangesNothing()
    {
        var author = Guid.NewGuid();
        var rate = IpcRate.Create(Guid.NewGuid(), 2026, 5.2m, author, Now);

        var changed = rate.Change(5.20m, Guid.NewGuid(), Now.AddDays(1));

        Assert.False(changed);
        Assert.Equal(1, rate.Version);
        Assert.Equal(author, rate.UpdatedByMemberId);
        Assert.Equal(Now, rate.UpdatedAt);
    }

    [Fact]
    public void AnotherRateBumpsTheVersion()
    {
        var rate = IpcRate.Create(Guid.NewGuid(), 2026, 5.2m, Guid.NewGuid(), Now);
        var other = Guid.NewGuid();

        var changed = rate.Change(5.3m, other, Now.AddDays(1));

        Assert.True(changed);
        Assert.Equal(5.3m, rate.Rate);
        Assert.Equal(2, rate.Version);
        Assert.Equal(other, rate.UpdatedByMemberId);
        Assert.Equal(Now.AddDays(1), rate.UpdatedAt);
    }

    [Theory]
    [InlineData(1999, 5)]
    [InlineData(2026, -10.01)]
    [InlineData(2026, 100.01)]
    public void AYearOrRateOutOfRangeIsRefused(int year, double rate) =>
        Assert.Throws<ServiceContractsDomainException>(() => IpcRate.Create(Guid.NewGuid(), year, (decimal)rate, null, Now));
}
