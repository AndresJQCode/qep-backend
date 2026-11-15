using Modules.Buyers.Application;
using Modules.Identity.Application;

namespace Bootstrapper.Valkey.Sessions;

/// <summary>
/// La caché de sesión de Identity con el área apagada (V3 del plan base): nunca acierta y no guarda nada, así que cada request
/// lee Postgres exactamente como antes.
/// </summary>
internal sealed class NullSessionCache : ISessionCache
{
    public Task<CachedSession?> GetAsync(string tokenHash, CancellationToken cancellationToken) =>
        Task.FromResult<CachedSession?>(null);

    public Task SetAsync(string tokenHash, CachedSession session, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RemoveAsync(IReadOnlyCollection<RevokedSession> sessions, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>La caché de sesión del comprador con el área apagada: nunca acierta y no guarda nada.</summary>
internal sealed class NullBuyerSessionCache : IBuyerSessionCache
{
    public Task<CachedBuyerSession?> GetAsync(string tokenHash, CancellationToken cancellationToken) =>
        Task.FromResult<CachedBuyerSession?>(null);

    public Task SetAsync(string tokenHash, CachedBuyerSession session, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task RemoveAsync(IReadOnlyCollection<RevokedBuyerSession> sessions, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
