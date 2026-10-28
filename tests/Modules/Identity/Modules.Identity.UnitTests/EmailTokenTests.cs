using Modules.Identity.Domain;

namespace Modules.Identity.UnitTests;

public sealed class EmailTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(EmailTokenPurpose.Verify, 24)]
    [InlineData(EmailTokenPurpose.Reset, 1)]
    public void IssueSetsTheExpiryByPurpose(EmailTokenPurpose purpose, int hours)
        => Assert.Equal(
            Now.AddHours(hours),
            EmailToken.Issue(EmailTokenId.New(), UserId.New(), purpose, "h", Now).ExpiresAt);

    [Fact]
    public void ATokenIsUnusableAtItsExpiryAndAfterInvalidation()
    {
        var token = EmailToken.Issue(EmailTokenId.New(), UserId.New(), EmailTokenPurpose.Reset, "h", Now);

        Assert.True(token.IsUsable(Now.AddMinutes(59)));
        Assert.False(token.IsUsable(Now.AddHours(1)));
        token.Invalidate(Now.AddMinutes(1));
        Assert.False(token.IsUsable(Now.AddMinutes(2)));
    }
}
