using Modules.Identity.Domain;

namespace Modules.Identity.UnitTests;

public sealed class PasswordCredentialTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateRejectsAnEmptyHash()
        => Assert.Equal(
            "identity.password.hash_required",
            Assert.Throws<IdentityDomainException>(() => PasswordCredential.Create(UserId.New(), " ", Now)).Code);

    [Fact]
    public void ReplaceChangesTheHashAndTheTimestamp()
    {
        var credential = PasswordCredential.Create(UserId.New(), "h1", Now);

        credential.Replace("h2", Now.AddMinutes(1));

        Assert.Equal("h2", credential.Hash);
        Assert.Equal(Now.AddMinutes(1), credential.UpdatedAt);
    }
}
