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
