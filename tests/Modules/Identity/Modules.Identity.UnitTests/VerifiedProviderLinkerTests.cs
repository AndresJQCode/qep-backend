using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.UnitTests;

public sealed class VerifiedProviderLinkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly FakePasswordCredentialRepository credentials = new();
    private readonly FakeEmailTokenRepository tokens = new();
    private readonly FakeUserRepository users = new();
    private readonly CountingIdentityUnitOfWork unitOfWork = new();
    private readonly VerifiedProviderLinker linker;
    private readonly OwnerProvisioningService provisioning;
    private readonly User user = User.CreateInvited(UserId.New(), "a@x.com", Now);

    public VerifiedProviderLinkerTests()
    {
        var clock = new FixedClock(Now);
        linker = new VerifiedProviderLinker(credentials, tokens, clock);
        provisioning = new OwnerProvisioningService(users, linker, unitOfWork, clock);
        credentials.Stored.Add(PasswordCredential.Create(user.Id, "hash", Now));
    }

    [Fact]
    public async Task LinkingAnUnverifiedUserRemovesThePasswordAndInvalidatesAllTokens()
    {
        await linker.LinkAsync(user, "google", "sub-1", TestContext.Current.CancellationToken);

        Assert.Equal(user.Id.Value, Assert.Single(credentials.Removed).UserId.Value);
        Assert.Equal((user.Id.Value, (EmailTokenPurpose?)null), Assert.Single(tokens.Invalidations));
        Assert.NotNull(user.EmailVerifiedAt);
    }

    [Fact]
    public async Task LinkingAVerifiedUserKeepsThePassword()
    {
        user.MarkEmailVerified(Now);

        await linker.LinkAsync(user, "google", "sub-1", TestContext.Current.CancellationToken);

        Assert.Empty(credentials.Removed);
        Assert.Empty(tokens.Invalidations);
    }

    [Theory]
    [InlineData("a@x.com", false)]
    [InlineData("   ", true)]
    [InlineData(null, true)]
    public async Task OwnerProvisioningDeniesAMissingOrUnverifiedEmailWithoutCreatingAUser(
        string? email,
        bool emailVerified)
    {
        var outcome = await provisioning.ProvisionOwnerAsync(
            "google", "sub-1", email, emailVerified, TestContext.Current.CancellationToken);

        Assert.Equal("email_not_verified", outcome.DenialReason);
        Assert.Empty(users.Added);
        Assert.Equal(0, unitOfWork.Saves);
    }
}
