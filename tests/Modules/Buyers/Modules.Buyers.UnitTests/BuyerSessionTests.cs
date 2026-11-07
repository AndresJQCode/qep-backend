using Modules.Buyers.Domain;

namespace Modules.Buyers.UnitTests;

public sealed class BuyerSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static BuyerSession NewSession() =>
        BuyerSession.Issue(Guid.NewGuid(), Guid.NewGuid(), "a.lulocrm.com", "hash", Now, "203.0.113.5");

    [Fact]
    public void ASessionIsValidOnlyForItsOwnHost()
    {
        var session = NewSession();
        Assert.True(session.IsValidFor("a.lulocrm.com", Now));
        Assert.False(session.IsValidFor("b.lulocrm.com", Now));
    }

    [Fact]
    public void ASessionExpiresAfterThirtyDaysEvenIfUsed()
    {
        var session = NewSession();
        for (var day = 1; day <= 29; day++) session.Touch(Now.AddDays(day));
        Assert.True(session.IsValidFor("a.lulocrm.com", Now.AddDays(29)));
        session.Touch(Now.AddDays(30).AddMinutes(-1));
        Assert.False(session.IsValidFor("a.lulocrm.com", Now.AddDays(30)));
    }

    [Fact]
    public void ASessionExpiresAfterSevenIdleDays()
    {
        var session = NewSession();
        Assert.True(session.IsValidFor("a.lulocrm.com", Now.AddDays(7).AddMinutes(-1)));
        Assert.False(session.IsValidFor("a.lulocrm.com", Now.AddDays(7).AddMinutes(1)));
    }

    [Fact]
    public void TouchOnlyWritesAfterFiveMinutes()
    {
        var session = NewSession();
        Assert.False(session.Touch(Now.AddMinutes(4)));
        Assert.True(session.Touch(Now.AddMinutes(6)));
        Assert.Equal(Now.AddMinutes(6), session.LastSeenAt);
    }

    [Fact]
    public void ARevokedSessionIsNotValid()
    {
        var session = NewSession();
        session.Revoke(Now.AddMinutes(1), "logout");
        Assert.False(session.IsValidFor("a.lulocrm.com", Now.AddMinutes(2)));
        Assert.Equal("logout", session.RevokedReason);
        Assert.Equal(Now.AddMinutes(1), session.RevokedAt);
    }

    [Fact]
    public void IssueTruncatesTheIpTo45Characters()
    {
        var session = BuyerSession.Issue(Guid.NewGuid(), Guid.NewGuid(), "a.lulocrm.com", "hash", Now, new string('1', 60));
        Assert.Equal(45, session.IpAddress!.Length);
    }
}
