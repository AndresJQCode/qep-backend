using Modules.Buyers.Domain;

namespace Modules.Buyers.UnitTests;

public sealed class BuyerEmailTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VerifyTokensLastTwentyFourHoursAndResetTokensOneHour()
    {
        var verify = BuyerEmailToken.Issue(Guid.NewGuid(), Guid.NewGuid(), BuyerEmailTokenPurpose.Verify, "h", Now);
        var reset = BuyerEmailToken.Issue(Guid.NewGuid(), Guid.NewGuid(), BuyerEmailTokenPurpose.Reset, "h", Now);
        Assert.Equal(Now.AddHours(24), verify.ExpiresAt);
        Assert.Equal(Now.AddHours(1), reset.ExpiresAt);
        Assert.Null(verify.UsedAt);
    }
}
