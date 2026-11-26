using Modules.Store.Domain;

namespace Modules.Store.Application;

public interface IOnlineStoreRepository
{
    /// <summary>Rastreada: el manejador la modifica y guarda. Las monedas llegan en el orden en que se guardaron.</summary>
    Task<OnlineStore?> FindAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(OnlineStore store);

    /// <summary>
    /// Otro tenant ya usa ese slug. Es el chequeo amable; la garantía es <c>IX_stores_slug</c>, que el
    /// unit of work traduce a <c>store.settings.slug_taken</c>.
    /// </summary>
    Task<bool> SlugTakenByAnotherAsync(Guid tenantId, string slug, CancellationToken cancellationToken);
}
