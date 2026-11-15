using Modules.Buyers.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.UnitTests;

/// <summary>
/// Borrar la cuenta con la caché de sesión (spec 2026-11-13, D-4). La cascada se lleva las sesiones sin nombrarlas, así que el
/// borrado las lista para sacar sus entradas. Lo hace con la cuenta bloqueada (<c>FOR UPDATE</c>) dentro de la transacción del
/// borrado: el <c>INSERT</c> de la sesión de un login concurrente toma <c>FOR KEY SHARE</c> sobre la cuenta por la clave foránea,
/// así que o confirmó antes del candado y la lista la ve, o espera y falla contra la cuenta ya borrada. Sin el candado, una sesión
/// confirmada entre la lista y el borrado quedaba en la caché de una cuenta que ya no existe, y los pedidos del comprador
/// (<c>BuyerOrders</c>) sólo miran <c>session.AccountId</c>.
/// </summary>
public sealed class DeleteBuyerAccountTests
{
    private const string Host = "acme.lulocrm.com";
    private const string Password = "una-contrasena-larga";

    [Fact]
    public async Task TheAccountIsLockedBeforeItsSessionsAreListedAndTheirEntriesRemovedAroundTheCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = new BuyersWorld();
        var account = world.AddVerifiedAccount("ana@correo.com", $"hash::{Password}");
        var sessions = new BuyerSessions(world.Sessions, world.UnitOfWork, world.Clock, world.SessionCache);
        var (token, _) = await sessions.IssueAsync(account.Id, Host, null, ct);
        // El login que confirma mientras el borrado espera el candado.
        world.OnAccountLock = () => world.SavedSessions.Add(
            BuyerSession.Issue(Guid.NewGuid(), account.Id, Host, "late-login-hash", world.Clock.UtcNow, null));
        world.Steps.Clear();
        var handler = new DeleteBuyerAccountHandler(
            new FakeStorefronts(), world.Accounts, world.Hasher, sessions, world.Sessions, world.SessionCache, world.Outbox,
            world.UnitOfWork, new DeleteBuyerAccountBodyValidator(), world.Clock);

        await handler.HandleAsync(new DeleteBuyerAccountCommand(Host, token, new DeleteBuyerAccountBody(Password)), ct);

        Assert.Equal(["lock", "list", "remove", "commit", "remove"], world.Steps);
        Assert.Contains("late-login-hash", world.SessionCache.RemovedHashes);
        Assert.Contains(BuyerTokenCodec.Hash(token), world.SessionCache.RemovedHashes);
    }
}
