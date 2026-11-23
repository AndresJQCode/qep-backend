using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class MembershipPhoneTests
{
    private const string Name = "Ana Pérez";
    private static readonly DateTimeOffset At = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NormalizeTrimsAndKeepsAValidE164() =>
        Assert.Equal("+573001234567", MemberPhone.Normalize(" +573001234567 "));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void NormalizeTurnsBlankIntoNull(string? value) =>
        Assert.Null(MemberPhone.Normalize(value));

    [Theory]
    [InlineData("3001234567")]
    [InlineData("+0573001234567")]
    [InlineData("+57 300 123 4567")]
    [InlineData("+1234567")]
    [InlineData("+1234567890123456")]
    // Arabic-Indic digits: .NET's \d matches any Unicode decimal digit, and WhatsApp cannot dial them.
    [InlineData("+57٣٠٠١٢٣٤٥٦٧")]
    public void NormalizeRejectsWhatIsNotE164(string value)
    {
        var error = Assert.Throws<TenantDomainException>(() => MemberPhone.Normalize(value));

        Assert.Equal("tenancy.membership.phone_invalid", error.Code);
    }

    [Fact]
    public void UpdateProfileWithAPhoneBumpsTheVersionOnce()
    {
        var membership = NewMembership();
        var version = membership.Version;

        var changes = membership.UpdateProfile(Name, null, PhoneChange.Set("+573001234567"), At.AddHours(1));

        Assert.True(changes.Phone);
        Assert.False(changes.Profile);
        Assert.True(changes.Any);
        Assert.Equal("+573001234567", membership.PhoneE164);
        Assert.Equal(version + 1, membership.Version);
    }

    [Fact]
    public void UpdateProfileWithNameAndPhoneBumpsTheVersionOnce()
    {
        var membership = NewMembership();
        var version = membership.Version;

        var changes = membership.UpdateProfile("Ana María", 7, PhoneChange.Set("+573001234567"), At.AddHours(1));

        Assert.True(changes.Phone);
        Assert.True(changes.Profile);
        Assert.Equal(version + 1, membership.Version);
    }

    [Fact]
    public void UpdateProfileKeepingThePhoneLeavesIt()
    {
        var membership = NewMembership();
        membership.ChangeOwnPhone("+573001234567", At.AddHours(1));
        var version = membership.Version;

        var changes = membership.UpdateProfile("Ana María", null, PhoneChange.Keep, At.AddHours(2));

        Assert.False(changes.Phone);
        Assert.True(changes.Profile);
        Assert.Equal("+573001234567", membership.PhoneE164);
        Assert.Equal(version + 1, membership.Version);
    }

    [Fact]
    public void UpdateProfileWithNullPhoneClearsIt()
    {
        var membership = NewMembership();
        membership.ChangeOwnPhone("+573001234567", At.AddHours(1));

        var changes = membership.UpdateProfile(Name, null, PhoneChange.Set(null), At.AddHours(2));

        Assert.True(changes.Phone);
        Assert.Null(membership.PhoneE164);
    }

    [Fact]
    public void ChangeOwnPhoneWithTheSameValueChangesNothing()
    {
        var membership = NewMembership();
        membership.ChangeOwnPhone("+573001234567", At.AddHours(1));
        var version = membership.Version;

        var changed = membership.ChangeOwnPhone("+573001234567", At.AddHours(2));

        Assert.False(changed);
        Assert.Equal(version, membership.Version);
    }

    private static Membership NewMembership() =>
        Membership.Invite(
            MembershipId.New(),
            Guid.CreateVersion7(),
            TenantId.New(),
            Name,
            ["advisor"],
            "invitation",
            "token",
            "token-hash",
            At,
            Membership.DefaultInvitationTimeToLive);
}
