using BuildingBlocks.Application;
using Modules.Audit.Domain;
using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.UnitTests;

public sealed class PasswordLoginServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private const string Email = "owner@example.com";
    private const string Pw = "correct horse battery";

    private readonly FakeUserRepository users = new();
    private readonly FakePasswordCredentialRepository credentials = new();
    private readonly FakePasswordHasher hasher = new();
    private readonly FakeEmailTokenIssuer tokens = new();
    private readonly RecordingIdentityOutbox outbox = new();
    private readonly RecordingIdentityAuditRecorder audit = new();
    private readonly CountingIdentityUnitOfWork unitOfWork = new();
    private readonly PasswordLoginService service;
    private readonly User user;
    private readonly PasswordCredential credential;

    public PasswordLoginServiceTests()
    {
        var clock = new FixedClock(Now);
        user = User.CreateInvited(UserId.New(), Email, Now);
        user.MarkEmailVerified(Now);
        user.Activate(Now);
        users.Stored.Add(user);
        credential = PasswordCredential.Create(user.Id, "h", Now);
        credentials.Stored.Add(credential);
        service = new PasswordLoginService(
            users,
            credentials,
            new PasswordCredentialVerifier(hasher, clock),
            tokens,
            outbox,
            audit,
            unitOfWork,
            new PasswordLoginValidator(),
            clock);
    }

    [Fact]
    public async Task AnUnknownEmailStillRunsTheHasherAgainstTheDummy()
    {
        await Assert.ThrowsAsync<RequestUnauthorizedException>(
            () => service.LoginAsync("nobody@x.com", "whatever pw", "c", TestContext.Current.CancellationToken));

        Assert.Equal([(string?)null], hasher.VerifiedHashes);
    }

    [Fact]
    public async Task AMalformedEmailIsTreatedAsUnknown()
    {
        var exception = await Assert.ThrowsAsync<RequestUnauthorizedException>(
            () => service.LoginAsync("not an email", Pw, "c", TestContext.Current.CancellationToken));

        Assert.Equal("identity.credentials.invalid", exception.Code);
        Assert.Equal([(string?)null], hasher.VerifiedHashes);
    }

    [Fact]
    public async Task AUserWithoutACredentialGetsTheSameErrorAsAnUnknownEmail()
    {
        credentials.Stored.Clear();

        var exception = await Assert.ThrowsAsync<RequestUnauthorizedException>(
            () => service.LoginAsync(Email, Pw, "c", TestContext.Current.CancellationToken));

        Assert.Equal("identity.credentials.invalid", exception.Code);
        Assert.Equal("Invalid credentials.", exception.Message);
        Assert.Equal([(string?)null], hasher.VerifiedHashes);
    }

    [Fact]
    public async Task AWrongPasswordIsAuditedAgainstTheUserAndNeverRehashes()
    {
        hasher.NextOutcome = PasswordVerificationOutcome.Failed;

        await Assert.ThrowsAsync<RequestUnauthorizedException>(
            () => service.LoginAsync(Email, Pw, "c", TestContext.Current.CancellationToken));

        Assert.Equal(
            (user.Id.Value, "identity.password.login.failed", user.Id.ToString(), AuditActorType.Human),
            Assert.Single(audit.Entries));
        Assert.Equal("h", credential.Hash);
        Assert.Equal(1, unitOfWork.Saves);
    }

    [Fact]
    public async Task AnUnknownEmailIsAuditedAsSystemWithoutTheEmail()
    {
        await Assert.ThrowsAsync<RequestUnauthorizedException>(
            () => service.LoginAsync("nobody@x.com", Pw, "c", TestContext.Current.CancellationToken));

        Assert.Equal(
            (Guid.Empty, "identity.password.login.failed", "unknown", AuditActorType.System),
            Assert.Single(audit.Entries));
        Assert.Equal(1, unitOfWork.Saves);
    }

    [Fact]
    public async Task LoginRehashesWhenTheHasherAsks()
    {
        hasher.NextOutcome = PasswordVerificationOutcome.SuccessRehashNeeded;

        await service.LoginAsync(Email, Pw, "c", TestContext.Current.CancellationToken);

        Assert.Equal("rehashed", credential.Hash);
        Assert.Equal(1, unitOfWork.Saves);
    }

    [Fact]
    public async Task NormalizesTheEmailBeforeTheLookup()
    {
        var result = await service.LoginAsync(
            "  OWNER@Example.COM ", Pw, "c", TestContext.Current.CancellationToken);

        Assert.Equal(user.Id.Value, result.UserId);
    }

    [Fact]
    public async Task AnUnverifiedEmailWithTheRightPasswordReissuesTheVerifyToken()
    {
        var unverified = User.CreateInvited(UserId.New(), "new@example.com", Now);
        users.Stored.Add(unverified);
        credentials.Stored.Add(PasswordCredential.Create(unverified.Id, "h2", Now));

        var exception = await Assert.ThrowsAsync<RequestForbiddenException>(
            () => service.LoginAsync("new@example.com", Pw, "c", TestContext.Current.CancellationToken));

        Assert.Equal("identity.email.not_verified", exception.Code);
        Assert.Equal((unverified.Id.Value, EmailTokenPurpose.Verify), Assert.Single(tokens.Issued));
        var added = Assert.Single(outbox.Added);
        Assert.Equal(IdentityIntegrationEvents.EmailVerificationRequested, added.EventName);
        Assert.Equal(new EmailTokenIssuedPayload(unverified.Id.Value, "raw-token"), added.Payload);
        Assert.Equal(1, unitOfWork.Saves);
    }

    [Fact]
    public async Task AWrongPasswordOnAnUnverifiedEmailReissuesNothing()
    {
        var unverified = User.CreateInvited(UserId.New(), "new@example.com", Now);
        users.Stored.Add(unverified);
        credentials.Stored.Add(PasswordCredential.Create(unverified.Id, "h2", Now));
        hasher.NextOutcome = PasswordVerificationOutcome.Failed;

        await Assert.ThrowsAsync<RequestUnauthorizedException>(
            () => service.LoginAsync("new@example.com", Pw, "c", TestContext.Current.CancellationToken));

        Assert.Empty(tokens.Issued);
        Assert.Empty(outbox.Added);
    }

    [Fact]
    public async Task ASuccessfulLoginIsAuditedAndReturnsTheUser()
    {
        var result = await service.LoginAsync(Email, Pw, "c", TestContext.Current.CancellationToken);

        Assert.Equal(new AuthenticatedUser(user.Id.Value, Email), result);
        Assert.Equal(
            (user.Id.Value, "identity.password.login.succeeded", user.Id.ToString(), AuditActorType.Human),
            Assert.Single(audit.Entries));
        Assert.Equal(1, unitOfWork.Saves);
    }
}
