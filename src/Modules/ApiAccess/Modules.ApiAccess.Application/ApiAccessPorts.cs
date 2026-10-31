using Modules.ApiAccess.Domain;

namespace Modules.ApiAccess.Application;

public interface IApiKeyRepository
{
    void Add(ApiKey key);

    Task<ApiKey?> FindAsync(Guid tenantId, ApiKeyId id, CancellationToken cancellationToken);

    /// <summary>Las claves del tenant, de la más nueva a la más vieja (<c>created_at DESC, id DESC</c>).</summary>
    Task<IReadOnlyList<ApiKey>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Las claves sin revocar y sin vencer: las que cuentan contra el tope por tenant.</summary>
    Task<int> CountActiveAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Búsqueda entre tenants, porque quien presenta la clave aún no está autenticado. Sin seguimiento:
    /// solo se lee para validar.
    /// </summary>
    Task<ApiKey?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken);

    /// <summary>Marca el último uso a lo sumo una vez por minuto, fuera de cualquier transacción.</summary>
    Task TouchLastUsedAsync(ApiKeyId id, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IApiAccessUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
