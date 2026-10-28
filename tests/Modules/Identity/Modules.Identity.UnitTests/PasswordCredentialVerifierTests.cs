using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.UnitTests;

public sealed class PasswordCredentialVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly FakePasswordHasher hasher = new();

    [Fact]
    public void ARehashRequestReplacesTheHash()
    {
        hasher.NextOutcome = PasswordVerificationOutcome.SuccessRehashNeeded;
        var credential = PasswordCredential.Create(UserId.New(), "old", Now);

        Assert.True(new PasswordCredentialVerifier(hasher, new FixedClock(Now)).Verify(credential, "pw"));

        Assert.Equal("rehashed", credential.Hash);
    }

    [Fact]
    public void AMissingCredentialRunsAgainstTheDummyAndFails()
    {
        Assert.False(new PasswordCredentialVerifier(hasher, new FixedClock(Now)).Verify(null, "pw"));

        Assert.Equal([(string?)null], hasher.VerifiedHashes);
    }

    [Fact]
    public void ASuccessKeepsTheHash()
    {
        var credential = PasswordCredential.Create(UserId.New(), "old", Now);

        Assert.True(new PasswordCredentialVerifier(hasher, new FixedClock(Now)).Verify(credential, "pw"));

        Assert.Equal("old", credential.Hash);
    }

    [Fact]
    public void AFailedVerificationNeitherSucceedsNorRehashes()
    {
        hasher.NextOutcome = PasswordVerificationOutcome.Failed;
        var credential = PasswordCredential.Create(UserId.New(), "old", Now);

        Assert.False(new PasswordCredentialVerifier(hasher, new FixedClock(Now)).Verify(credential, "pw"));

        Assert.Equal("old", credential.Hash);
    }
}
