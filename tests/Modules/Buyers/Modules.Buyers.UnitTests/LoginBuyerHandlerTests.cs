using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Buyers.Application;

namespace Modules.Buyers.UnitTests;

public sealed class LoginBuyerHandlerTests
{
    private const string Host = "acme.lulocrm.com";

    /// <summary>Anota los enlaces por correo y, si se le pide, falla.</summary>
    private sealed class RecordingOrderLinker : IBuyerOrderLinker
    {
        public List<(Guid TenantId, Guid AccountId, string Email)> EmailLinks { get; } = [];

        public Exception? Throws { get; set; }

        public List<CancellationToken> Tokens { get; } = [];

        public Task LinkAsync(Guid tenantId, Guid orderId, Guid accountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> LinkByEmailAsync(
            Guid tenantId, Guid accountId, string normalizedEmail, CancellationToken cancellationToken)
        {
            EmailLinks.Add((tenantId, accountId, normalizedEmail));
            Tokens.Add(cancellationToken);
            return Throws is null ? Task.FromResult(1) : throw Throws;
        }
    }

    private static LoginBuyerHandler Handler(BuyersWorld world, IBuyerPasswordHasher hasher, IBuyerOrderLinker linker) =>
        new(
            new FakeStorefronts(),
            world.Accounts,
            world.Profiles,
            hasher,
            new BuyerSessions(world.Sessions, world.UnitOfWork, world.Clock, world.SessionCache),
            world.UnitOfWork,
            new LoginBuyerBodyValidator(),
            world.Clock,
            linker,
            NullLogger<LoginBuyerHandler>.Instance);

    private static LoginBuyerCommand Login(string email, string password) =>
        new(Host, null, new LoginBuyerBody(email, password));

    [Fact]
    public async Task ALoginThatNeedsARehashStoresTheNewHash()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = new BuyersWorld();
        var account = world.AddVerifiedAccount("ana@correo.com", "old-hash::una-contrasena-larga");
        var handler = Handler(world, new RehashingHasher(), new RecordingOrderLinker());

        var result = await handler.HandleAsync(Login("ana@correo.com", "una-contrasena-larga"), cancellationToken);

        Assert.Equal("new-hash::una-contrasena-larga", account.PasswordHash);
        Assert.Equal(43, result.SessionToken.Length);
        Assert.Single(world.SavedSessions);
    }

    // Spec 2026-11-15 §3.2: después del commit de la sesión, los pedidos de la tienda del tenant con el correo de la cuenta.
    [Fact]
    public async Task LoginLinksTheStoreOrdersOfTheTenantByEmail()
    {
        var world = new BuyersWorld();
        var account = world.AddVerifiedAccount("ana@correo.com", "hash::una-contrasena-larga");
        var linker = new RecordingOrderLinker();

        await Handler(world, new CountingHasher(), linker)
            .HandleAsync(Login(" ANA@correo.com ", "una-contrasena-larga"), TestContext.Current.CancellationToken);

        Assert.Equal((FakeStorefronts.TenantId, account.Id, "ana@correo.com"), Assert.Single(linker.EmailLinks));
        Assert.Single(world.SavedSessions);
    }

    [Fact]
    public async Task LoginSucceedsWhenTheLinkFails()
    {
        var world = new BuyersWorld();
        world.AddVerifiedAccount("ana@correo.com", "hash::una-contrasena-larga");
        var linker = new RecordingOrderLinker { Throws = new InvalidOperationException("quotations down") };

        var result = await Handler(world, new CountingHasher(), linker)
            .HandleAsync(Login("ana@correo.com", "una-contrasena-larga"), TestContext.Current.CancellationToken);

        Assert.Equal(43, result.SessionToken.Length);
        Assert.Equal("ana@correo.com", result.Buyer.Email);
        Assert.Single(linker.EmailLinks);
    }

    // Revisión final: con la sesión ya confirmada, ni el token del request ni una cancelación del enlace hacen fallar el ingreso.
    [Fact]
    public async Task TheLinkRunsWithoutTheRequestTokenAndACancellationDoesNotFailTheLogin()
    {
        var world = new BuyersWorld();
        world.AddVerifiedAccount("ana@correo.com", "hash::una-contrasena-larga");
        var linker = new RecordingOrderLinker { Throws = new OperationCanceledException() };

        var result = await Handler(world, new CountingHasher(), linker)
            .HandleAsync(Login("ana@correo.com", "una-contrasena-larga"), TestContext.Current.CancellationToken);

        Assert.Equal("ana@correo.com", result.Buyer.Email);
        Assert.Equal(CancellationToken.None, Assert.Single(linker.Tokens));
    }

    [Fact]
    public async Task FailedLoginDoesNotLink()
    {
        var world = new BuyersWorld();
        world.AddVerifiedAccount("ana@correo.com", "hash::una-contrasena-larga");
        var linker = new RecordingOrderLinker();

        await Assert.ThrowsAsync<RequestUnauthorizedException>(() => Handler(world, new CountingHasher(), linker)
            .HandleAsync(Login("ana@correo.com", "otra-contrasena-mala"), TestContext.Current.CancellationToken));

        Assert.Empty(linker.EmailLinks);
    }
}
