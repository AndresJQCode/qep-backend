using Modules.Identity.Domain;

namespace Modules.Identity.UnitTests;

public sealed class UserTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateInvitedNormalizesEmailAndStartsInvited()
    {
        var user = User.CreateInvited(UserId.New(), "  Person@Example.COM ", CreatedAt);

        Assert.Equal("person@example.com", user.Email);
        Assert.Equal(UserStatus.Invited, user.Status);
        Assert.Empty(user.ProviderLinks);
    }

    [Fact]
    public void ActivateTransitionsInvitedToActiveAndIsIdempotent()
    {
        var user = User.CreateInvited(UserId.New(), "person@example.com", CreatedAt);

        user.Activate(CreatedAt.AddMinutes(1));
        Assert.Equal(UserStatus.Active, user.Status);

        // Una segunda activación es un no-op, no una falla.
        user.Activate(CreatedAt.AddMinutes(2));
        Assert.Equal(UserStatus.Active, user.Status);
    }

    [Fact]
    public void LinkProviderAddsLinkAndIsIdempotentForSameSubject()
    {
        var user = User.CreateInvited(UserId.New(), "person@example.com", CreatedAt);

        var first = user.LinkProvider("Google", "sub-123", CreatedAt);
        var second = user.LinkProvider("google", "sub-123", CreatedAt.AddMinutes(1));

        Assert.Same(first, second);
        Assert.Equal("google", Assert.Single(user.ProviderLinks).Provider);
        Assert.Equal(user.Id, first.UserId);
    }

    [Fact]
    public void LinkProviderRejectsDifferentSubjectForSameProvider()
    {
        var user = User.CreateInvited(UserId.New(), "person@example.com", CreatedAt);
        user.LinkProvider("google", "sub-123", CreatedAt);

        var exception = Assert.Throws<IdentityDomainException>(() =>
            user.LinkProvider("google", "sub-999", CreatedAt.AddMinutes(1)));

        Assert.Equal("identity.provider_link.conflict", exception.Code);
    }

    [Fact]
    public void CreateInvitedWithInvalidEmailThrowsDomainException()
    {
        var exception = Assert.Throws<IdentityDomainException>(() =>
            User.CreateInvited(UserId.New(), "not-an-email", CreatedAt));

        Assert.Equal("identity.email.invalid", exception.Code);
    }
}
