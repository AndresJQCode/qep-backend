using Modules.Buyers.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.UnitTests;

public sealed class BuyerSessionsTests
{
    private const string Host = "acme.lulocrm.com";

    private static BuyerSessions Sessions(BuyersWorld world) =>
        new(world.Sessions, world.UnitOfWork, world.Clock, world.SessionCache);

    private static CachedBuyerSession CachedFor(Guid accountId, string host, DateTimeOffset lastSeenAt) =>
        new(Guid.NewGuid(), accountId, host, BuyersWorld.Now.AddDays(-1), lastSeenAt, BuyersWorld.Now.AddDays(20));

    [Fact]
    public async Task ACachedSessionIsServedWithoutTheRepository()
    {
        var world = new BuyersWorld();
        var token = BuyerTokenCodec.Generate();
        var accountId = Guid.NewGuid();
        var cached = CachedFor(accountId, Host, BuyersWorld.Now.AddMinutes(-1));
        world.SessionCache.Entries[BuyerTokenCodec.Hash(token)] = cached;

        var session = await Sessions(world).TryResolveAsync(Host, token, TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.Equal((cached.SessionId, accountId), (session.Id, session.AccountId));
        Assert.Equal(0, world.Sessions.FindCalls);
    }

    [Fact]
    public async Task ACachedSessionOfAnotherHostIsNotServed()
    {
        var world = new BuyersWorld();
        var token = BuyerTokenCodec.Generate();
        world.SessionCache.Entries[BuyerTokenCodec.Hash(token)] =
            CachedFor(Guid.NewGuid(), "otra.lulocrm.com", BuyersWorld.Now.AddMinutes(-1));

        Assert.Null(await Sessions(world).TryResolveAsync(Host, token, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACachedSessionPastTheTouchThresholdGoesToTheDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = new BuyersWorld();
        var sessions = Sessions(world);
        var (token, _) = await sessions.IssueAsync(Guid.NewGuid(), Host, null, cancellationToken);
        var stored = world.SavedSessions.Single();
        world.SessionCache.Entries[BuyerTokenCodec.Hash(token)] = new CachedBuyerSession(
            stored.Id, stored.AccountId, Host, stored.CreatedAt, stored.LastSeenAt, stored.ExpiresAt);
        world.Clock.UtcNow = BuyersWorld.Now.AddMinutes(6);

        Assert.NotNull(await sessions.TryResolveAsync(Host, token, cancellationToken));

        Assert.Equal(1, world.Sessions.FindCalls);
        Assert.Equal(BuyersWorld.Now.AddMinutes(6), stored.LastSeenAt);
        Assert.Equal(BuyersWorld.Now.AddMinutes(6), world.SessionCache.Entries[BuyerTokenCodec.Hash(token)].LastSeenAt);
    }

    [Fact]
    public async Task RevokeAllRemovesTheReturnedHashesBeforeAndAfterSaving()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = new BuyersWorld();
        var sessions = Sessions(world);
        var accountId = Guid.NewGuid();
        await sessions.IssueAsync(accountId, Host, null, cancellationToken);
        await sessions.IssueAsync(accountId, Host, null, cancellationToken);
        world.Steps.Clear();

        await sessions.RevokeAllAsync(accountId, BuyerSession.RevokedReasons.PasswordReset, cancellationToken);

        Assert.Equal(["remove", "commit", "remove"], world.Steps);
        Assert.Equal(
            world.SavedSessions.Select(session => session.TokenHash).Order(),
            world.SessionCache.RemovedHashes.Distinct().Order());
    }

    // P3: el logout ya no revoca la entidad que devolvió TryResolveAsync (podría venir de la caché, sin seguimiento de EF).
    [Fact]
    public async Task RevokeCurrentReadsTheDatabaseAndRemovesTheEntryAroundTheSave()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = new BuyersWorld();
        var sessions = Sessions(world);
        var (token, _) = await sessions.IssueAsync(Guid.NewGuid(), Host, null, cancellationToken);
        Assert.NotNull(await sessions.TryResolveAsync(Host, token, cancellationToken));
        world.Steps.Clear();

        Assert.True(await sessions.RevokeCurrentAsync(Host, token, BuyerSession.RevokedReasons.Logout, cancellationToken));

        Assert.Equal(["remove", "commit", "remove"], world.Steps);
        Assert.Equal(BuyerSession.RevokedReasons.Logout, world.SavedSessions.Single().RevokedReason);
        Assert.Null(await sessions.TryResolveAsync(Host, token, cancellationToken));
    }

    [Fact]
    public async Task AnOversizedTokenIsRejectedWithoutALookup()
    {
        var world = new BuyersWorld();
        var sessions = Sessions(world);

        Assert.Null(await sessions.TryResolveAsync(Host, new string('a', 129), TestContext.Current.CancellationToken));
        Assert.Null(await sessions.TryResolveAsync(Host, "", TestContext.Current.CancellationToken));
        Assert.Null(await sessions.TryResolveAsync(Host, null, TestContext.Current.CancellationToken));

        Assert.Equal(0, world.Sessions.FindCalls);
    }

    [Fact]
    public async Task ResolveTouchesOnlyAfterFiveMinutes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = new BuyersWorld();
        var sessions = Sessions(world);
        var (token, _) = await sessions.IssueAsync(Guid.NewGuid(), Host, null, cancellationToken);
        var savesAfterIssue = world.SaveCalls;

        world.Clock.UtcNow = BuyersWorld.Now.AddMinutes(4);
        Assert.NotNull(await sessions.TryResolveAsync(Host, token, cancellationToken));
        Assert.Equal(savesAfterIssue, world.SaveCalls);

        world.Clock.UtcNow = BuyersWorld.Now.AddMinutes(6);
        Assert.NotNull(await sessions.TryResolveAsync(Host, token, cancellationToken));
        Assert.Equal(savesAfterIssue + 1, world.SaveCalls);
        Assert.Equal(BuyersWorld.Now.AddMinutes(6), world.SavedSessions.Single().LastSeenAt);
    }

    [Fact]
    public async Task OnlyTheHashOfTheTokenIsStored()
    {
        var world = new BuyersWorld();

        var (token, expiresAt) = await Sessions(world).IssueAsync(
            Guid.NewGuid(), Host, null, TestContext.Current.CancellationToken);

        var stored = world.SavedSessions.Single();
        Assert.Equal(43, token.Length);
        Assert.Equal(BuyerTokenCodec.Hash(token), stored.TokenHash);
        Assert.NotEqual(token, stored.TokenHash);
        Assert.Equal(BuyersWorld.Now + BuyerSession.AbsoluteLifetime, expiresAt);
    }

    [Fact]
    public async Task AnExpiredIdleOrForeignSessionDoesNotResolveAndIsNotTouched()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = new BuyersWorld();
        var sessions = Sessions(world);
        var (token, _) = await sessions.IssueAsync(Guid.NewGuid(), Host, null, cancellationToken);
        var saves = world.SaveCalls;

        Assert.Null(await sessions.TryResolveAsync("otra.lulocrm.com", token, cancellationToken));

        world.Clock.UtcNow = BuyersWorld.Now + BuyerSession.IdleTimeout + TimeSpan.FromMinutes(1);
        Assert.Null(await sessions.TryResolveAsync(Host, token, cancellationToken));
        Assert.Equal(saves, world.SaveCalls);
        await Assert.ThrowsAsync<BuildingBlocks.Application.RequestUnauthorizedException>(
            () => sessions.RequireAsync(Host, token, cancellationToken));
    }

    [Fact]
    public async Task RevokeOthersKeepsTheCurrentSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = new BuyersWorld();
        var sessions = Sessions(world);
        var accountId = Guid.NewGuid();
        await sessions.IssueAsync(accountId, Host, null, cancellationToken);
        await sessions.IssueAsync(accountId, Host, null, cancellationToken);
        var keep = world.SavedSessions[0];

        await sessions.RevokeOthersAsync(accountId, keep.Id, BuyerSession.RevokedReasons.PasswordChanged, cancellationToken);

        Assert.Null(keep.RevokedAt);
        Assert.Equal(BuyerSession.RevokedReasons.PasswordChanged, world.SavedSessions[1].RevokedReason);
    }
}
