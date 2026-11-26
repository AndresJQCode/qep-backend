using Bootstrapper.Valkey.Sessions;
using Modules.Identity.Application;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// La caché de sesión en memoria del pod, la que rige con <c>Valkey:Areas:Sessions</c> apagado: mismos TTL que la de Valkey
/// (<see cref="SessionCacheTtl"/>) y la misma marca de revocación, para que una entrada escrita tarde no resucite una sesión
/// revocada.
/// </summary>
public sealed class MemorySessionCacheTests
{
    private const string Hash = "0123456789abcdef";

    private static CachedSession SessionValidFor(ManualTimeProvider time, TimeSpan remaining) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), time.GetUtcNow(), time.GetUtcNow() + remaining);

    [Fact]
    public async Task ASetEntryIsReadBack()
    {
        var time = new ManualTimeProvider();
        var cache = new MemorySessionCache(time);
        var session = SessionValidFor(time, TimeSpan.FromHours(1));

        await cache.SetAsync(Hash, session, CancellationToken.None);

        Assert.Equal(session, await cache.GetAsync(Hash, CancellationToken.None));
    }

    [Fact]
    public async Task AnEntryLivesAtMostFifteenMinutes()
    {
        var time = new ManualTimeProvider();
        var cache = new MemorySessionCache(time);
        await cache.SetAsync(Hash, SessionValidFor(time, TimeSpan.FromHours(1)), CancellationToken.None);

        time.Advance(SessionCacheTtl.MaxEntry + TimeSpan.FromSeconds(1));

        Assert.Null(await cache.GetAsync(Hash, CancellationToken.None));
    }

    [Fact]
    public async Task AnEntryNeverOutlivesItsValidity()
    {
        var time = new ManualTimeProvider();
        var cache = new MemorySessionCache(time);
        await cache.SetAsync(Hash, SessionValidFor(time, TimeSpan.FromMinutes(2)), CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(3));

        Assert.Null(await cache.GetAsync(Hash, CancellationToken.None));
    }

    [Fact]
    public async Task ASessionWithoutRemainingValidityIsNotStored()
    {
        var time = new ManualTimeProvider();
        var cache = new MemorySessionCache(time);

        await cache.SetAsync(Hash, SessionValidFor(time, TimeSpan.Zero), CancellationToken.None);

        Assert.Null(await cache.GetAsync(Hash, CancellationToken.None));
    }

    [Fact]
    public async Task RemovingDropsTheEntryAndBlocksALateSet()
    {
        var time = new ManualTimeProvider();
        var cache = new MemorySessionCache(time);
        var session = SessionValidFor(time, TimeSpan.FromHours(1));
        await cache.SetAsync(Hash, session, CancellationToken.None);

        await cache.RemoveAsync([new RevokedSession(session.SessionId, Hash)], CancellationToken.None);
        Assert.Null(await cache.GetAsync(Hash, CancellationToken.None));

        // El request que leyó Postgres antes del commit escribe después del segundo borrado: la marca gana.
        await cache.SetAsync(Hash, session, CancellationToken.None);
        Assert.Null(await cache.GetAsync(Hash, CancellationToken.None));
    }

    // La marca comparte el MemoryCache con las entradas: si la compactación por tamaño la sacara antes que la entrada vieja que
    // vigila, un request que leyó Postgres antes del commit volvería a cachear la sesión revocada.
    [Fact]
    public async Task TheRevocationMarkSurvivesCompactionBySize()
    {
        // Con el 5 % de compactación por defecto, un tope menor que 20 nunca compacta (el 5 % se trunca a cero).
        const int limit = 100;
        var time = new ManualTimeProvider();
        using var cache = new MemorySessionCache(time, limit);
        var session = SessionValidFor(time, TimeSpan.FromHours(1));
        await cache.RemoveAsync([new RevokedSession(session.SessionId, Hash)], CancellationToken.None);

        // Más entradas que el tope: MemoryCache rechaza las que no caben y compacta en segundo plano.
        for (var i = 0; i < limit * 3; i++)
        {
            await cache.SetAsync($"filler-{i}", SessionValidFor(time, TimeSpan.FromHours(1)), CancellationToken.None);
        }

        // La compactación corre en el pool: se espera a que haya sacado algo antes de mirar la marca.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (cache.Count >= limit && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(cache.Count < limit, "la compactación no corrió");

        await cache.SetAsync(Hash, session, CancellationToken.None);
        Assert.Null(await cache.GetAsync(Hash, CancellationToken.None));
    }

    [Fact]
    public async Task TheRevocationMarkExpiresAndTheHashCanBeCachedAgain()
    {
        var time = new ManualTimeProvider();
        var cache = new MemorySessionCache(time);
        await cache.RemoveAsync([new RevokedSession(Guid.CreateVersion7(), Hash)], CancellationToken.None);

        time.Advance(SessionCacheTtl.RevocationMarker + TimeSpan.FromSeconds(1));
        var session = SessionValidFor(time, TimeSpan.FromHours(1));
        await cache.SetAsync(Hash, session, CancellationToken.None);

        Assert.Equal(session, await cache.GetAsync(Hash, CancellationToken.None));
    }
}
