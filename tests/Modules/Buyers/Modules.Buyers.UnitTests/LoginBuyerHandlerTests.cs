using Modules.Buyers.Application;

namespace Modules.Buyers.UnitTests;

public sealed class LoginBuyerHandlerTests
{
    [Fact]
    public async Task ALoginThatNeedsARehashStoresTheNewHash()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = new BuyersWorld();
        var account = world.AddVerifiedAccount("ana@correo.com", "old-hash::una-contrasena-larga");
        var hasher = new RehashingHasher();
        var sessions = new BuyerSessions(world.Sessions, world.UnitOfWork, world.Clock);
        var handler = new LoginBuyerHandler(
            new FakeStorefronts(), world.Accounts, world.Profiles, hasher, sessions, new LoginBuyerBodyValidator(), world.Clock);

        var result = await handler.HandleAsync(
            new LoginBuyerCommand("acme.lulocrm.com", null, new LoginBuyerBody("ana@correo.com", "una-contrasena-larga")),
            cancellationToken);

        Assert.Equal("new-hash::una-contrasena-larga", account.PasswordHash);
        Assert.Equal(43, result.SessionToken.Length);
        Assert.Single(world.SavedSessions);
    }
}
