using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.UnitTests;

public sealed class BuyerSignupTests
{
    private const string Host = "acme.lulocrm.com";
    private const string Email = "ana@correo.com";

    private static BuyerProfileDto Profile(string name) => new(
        new BuyerBillingDto(name, "CC", "1020304050", "3001234567", Email, "Cra 1 # 2-3", "CO", Guid.NewGuid(), Guid.NewGuid(), null),
        null,
        true);

    // Mantiene el tipo CC, que España no admite.
    private static BuyerProfileDto Spanish(BuyerProfileDto p) =>
        p with { Billing = p.Billing with { Country = "ES", DepartmentId = null, CityId = null, CityName = "Madrid" } };

    private static BuyerSignupRequest Request(string hash, BuyerProfileDto? profile = null, string email = Email) =>
        new(Host, "Tienda Demo", "203.0.113.10", email, hash, profile);

    [Fact]
    public async Task ANewEmailCreatesAnUnverifiedAccountWithConsentAndQueuesVerification()
    {
        var world = new BuyersWorld();

        await world.Signup.SignUpAsync(Request("hash-1", Profile("Ana")), TestContext.Current.CancellationToken);

        var account = Assert.Single(world.SavedAccounts);
        Assert.Null(account.EmailVerifiedAt);
        Assert.Equal(Email, account.Email);
        Assert.Equal("hash-1", account.PasswordHash);
        Assert.Equal("Ana", Assert.Single(world.SavedProfiles).Data.Billing.Name);

        var consent = Assert.Single(world.SavedConsents);
        Assert.Equal("https://acme.lulocrm.com/cuenta/politica", consent.PolicyUrl);
        Assert.Equal("203.0.113.10", consent.VisitorIp);
        Assert.Equal(account.Id, consent.AccountId);

        var token = Assert.Single(world.SavedTokens);
        Assert.Equal(BuyerEmailTokenPurpose.Verify, token.Purpose);
        var queued = Assert.Single(world.EventsNamed(BuyerIntegrationEvents.EmailVerificationRequested));
        var payload = Assert.IsType<BuyerEmailPayload>(queued.Payload);
        Assert.Equal("Tienda Demo", payload.StoreName);
        Assert.Equal(Host, payload.StoreHost);
        Assert.Equal(Email, payload.Email);
        Assert.Equal(account.Id, payload.AccountId);
        Assert.NotNull(payload.Token);
        Assert.Equal(token.TokenHash, BuyerTokenCodec.Hash(payload.Token));
    }

    [Fact]
    public async Task ANewEmailWithoutAProfileStoresNoProfile()
    {
        var world = new BuyersWorld();

        await world.Signup.SignUpAsync(Request("hash-1"), TestContext.Current.CancellationToken);

        Assert.Empty(world.SavedProfiles);
    }

    [Fact]
    public async Task AVerifiedEmailOnlyQueuesTheExistingAccountNotice()
    {
        var world = new BuyersWorld();
        var existing = world.AddVerifiedAccount(Email, "hash-original");

        await world.Signup.SignUpAsync(Request("hash-new", Profile("Otra")), TestContext.Current.CancellationToken);

        Assert.Equal("hash-original", existing.PasswordHash);
        Assert.Empty(world.SavedProfiles);
        Assert.Empty(world.SavedConsents);
        Assert.Empty(world.SavedTokens);
        var queued = Assert.Single(world.SavedEvents);
        Assert.Equal(BuyerIntegrationEvents.ExistingAccountNotice, queued.EventName);
        var payload = Assert.IsType<BuyerEmailPayload>(queued.Payload);
        Assert.Null(payload.Token);
        Assert.Equal("Tienda Demo", payload.StoreName);
        Assert.Equal(existing.Id, payload.AccountId);
    }

    [Fact]
    public async Task AnUnverifiedEmailReplacesPasswordAndProfileAndReissuesVerification()
    {
        var world = new BuyersWorld();
        var cancellationToken = TestContext.Current.CancellationToken;
        await world.Signup.SignUpAsync(Request("hash-1", Profile("Primero")), cancellationToken);
        var firstToken = Assert.Single(world.SavedTokens);

        await world.Signup.SignUpAsync(Request("hash-2", Profile("Segundo")), cancellationToken);

        var account = Assert.Single(world.SavedAccounts);
        Assert.Equal("hash-2", account.PasswordHash);
        Assert.Equal("Segundo", Assert.Single(world.SavedProfiles).Data.Billing.Name);
        Assert.NotNull(firstToken.UsedAt);
        Assert.Equal(2, world.SavedTokens.Count);
        Assert.Null(world.SavedTokens[1].UsedAt);
        Assert.Equal(2, world.SavedConsents.Count);
        Assert.Equal(2, world.EventsNamed(BuyerIntegrationEvents.EmailVerificationRequested).Count);
    }

    [Fact]
    public async Task ASignupWithAProfileTheCountryDoesNotAllowCreatesTheAccountWithoutProfile()
    {
        var world = new BuyersWorld();

        await world.Signup.SignUpAsync(Request("hash-1", Spanish(Profile("Ana"))), TestContext.Current.CancellationToken);

        Assert.Single(world.SavedAccounts);
        Assert.Empty(world.SavedProfiles);
        Assert.Single(world.SavedTokens);
    }

    [Fact]
    public async Task AnUnverifiedEmailWithADisallowedProfileStillReRegistersAndClearsTheOldProfile()
    {
        var world = new BuyersWorld();
        var cancellationToken = TestContext.Current.CancellationToken;
        await world.Signup.SignUpAsync(Request("hash-1", Profile("Primero")), cancellationToken);
        var firstToken = Assert.Single(world.SavedTokens);

        await world.Signup.SignUpAsync(Request("hash-2", Spanish(Profile("Segundo"))), cancellationToken);

        var account = Assert.Single(world.SavedAccounts);
        Assert.Equal("hash-2", account.PasswordHash);
        Assert.Empty(world.SavedProfiles);
        Assert.NotNull(firstToken.UsedAt);
        Assert.Equal(2, world.SavedTokens.Count);
        Assert.Null(world.SavedTokens[1].UsedAt);
        Assert.Equal(2, world.SavedConsents.Count);
        Assert.Equal(2, world.EventsNamed(BuyerIntegrationEvents.EmailVerificationRequested).Count);
    }

    [Fact]
    public async Task AnUnverifiedEmailWithoutAProfileDeletesTheOldProfile()
    {
        var world = new BuyersWorld();
        var cancellationToken = TestContext.Current.CancellationToken;
        await world.Signup.SignUpAsync(Request("hash-1", Profile("Primero")), cancellationToken);

        await world.Signup.SignUpAsync(Request("hash-2"), cancellationToken);

        Assert.Empty(world.SavedProfiles);
    }

    [Fact]
    public async Task TheEmailIsNormalizedBeforeLookingTheAccountUp()
    {
        var world = new BuyersWorld();
        var existing = world.AddVerifiedAccount(Email, "hash-original");

        await world.Signup.SignUpAsync(Request("hash-new", email: "  ANA@Correo.COM "), TestContext.Current.CancellationToken);

        Assert.Single(world.SavedAccounts);
        var queued = Assert.Single(world.SavedEvents);
        Assert.Equal(BuyerIntegrationEvents.ExistingAccountNotice, queued.EventName);
        Assert.Equal(existing.Id, Assert.IsType<BuyerEmailPayload>(queued.Payload).AccountId);
    }

    [Fact]
    public async Task LosingTheRaceRetriesOnceThroughTheExistingEmailPath()
    {
        var world = new BuyersWorld();
        BuyerAccount? winner = null;
        world.WinTheRaceOnNextSave = () => winner = world.AddUnverifiedAccount(Email, "hash-winner");

        await world.Signup.SignUpAsync(Request("hash-loser", Profile("Perdedor")), TestContext.Current.CancellationToken);

        Assert.Equal(2, world.SaveCalls);
        var account = Assert.Single(world.SavedAccounts);
        Assert.Same(winner, account);
        Assert.Equal("hash-loser", account.PasswordHash);
        var queued = Assert.Single(world.SavedEvents);
        Assert.Equal(BuyerIntegrationEvents.EmailVerificationRequested, queued.EventName);
        Assert.Equal(account.Id, Assert.IsType<BuyerEmailPayload>(queued.Payload).AccountId);
        Assert.Single(world.SavedConsents);
    }

    [Fact]
    public async Task ASecondConflictIsNotRetriedAgain()
    {
        var world = new BuyersWorld();
        world.WinTheRaceOnNextSave = () => { };
        var signup = new BuyerSignup(
            world.Accounts, world.Consents, world.ProfileWriter, world.TokenIssuer, world.Outbox, new AlwaysConflicting(), world.Clock,
            NullLogger<BuyerSignup>.Instance);

        await Assert.ThrowsAsync<ResourceConflictException>(
            () => signup.SignUpAsync(Request("hash-1"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAccountPurgedBeforeTheSaveIsCreatedAgainOnTheRetry()
    {
        var world = new BuyersWorld();
        var purged = world.AddUnverifiedAccount(Email, "hash-old");
        world.ConflictOnNextSave = () => world.SavedAccounts.Remove(purged);

        await world.Signup.SignUpAsync(Request("hash-new"), TestContext.Current.CancellationToken);

        Assert.Equal(2, world.SaveCalls);
        var account = Assert.Single(world.SavedAccounts);
        Assert.NotEqual(purged.Id, account.Id);
        Assert.Equal("hash-new", account.PasswordHash);
    }

    [Fact]
    public async Task AnAccountVerifiedBeforeTheSaveOnlyGetsTheNoticeOnTheRetry()
    {
        var world = new BuyersWorld();
        var account = world.AddUnverifiedAccount(Email, "hash-old");
        // Lo que dejó el restablecimiento que confirmó primero (el doble no tiene tracker que revertir el primer intento).
        world.ConflictOnNextSave = () =>
        {
            account.ChangePasswordHash("hash-reset", BuyersWorld.Now);
            account.MarkEmailVerified(BuyersWorld.Now);
        };

        await world.Signup.SignUpAsync(Request("hash-new"), TestContext.Current.CancellationToken);

        Assert.Equal("hash-reset", account.PasswordHash);
        var queued = Assert.Single(world.SavedEvents);
        Assert.Equal(BuyerIntegrationEvents.ExistingAccountNotice, queued.EventName);
    }

    private sealed class AlwaysConflicting : IBuyersUnitOfWork
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken) =>
            throw new ResourceConflictException(BuyerErrorCodes.EmailTaken, "taken");

        public Task SaveChangesAsync(Func<CancellationToken, Task> afterSave, CancellationToken cancellationToken) =>
            SaveChangesAsync(cancellationToken);

        public void DiscardPendingChanges()
        {
        }
    }
}
