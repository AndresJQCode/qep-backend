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

/// <summary>
/// Registro de las llamadas a la API externa. Escribe con contexto propio y <b>nunca propaga sus
/// errores</b>: se invoca después de responder, y perder una fila no puede romper una llamada.
/// </summary>
public interface IApiKeyRequestLog
{
    Task RecordAsync(ApiKeyRequest request, CancellationToken cancellationToken);
}

public interface IApiAccessUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Las conexiones <c>whatsapp-cloud</c> del tenant, activas o no. ApiAccess no ve Messaging ni
/// Integrations: el adaptador vive en Bootstrapper.
/// </summary>
public interface IApiKeyConnectionDirectory
{
    /// <summary>Id y nombre de cada conexión del tenant. Una conexión borrada no aparece.</summary>
    Task<IReadOnlyDictionary<Guid, string>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>El estado de un mensaje enviado por clave, tal como lo cuenta Messaging.</summary>
public sealed record ApiMessageState(string Status, int? FailureCode, string? FailureReason);

/// <summary>
/// Los mensajes que las llamadas dejaron. ApiAccess no ve Messaging: el adaptador vive en Bootstrapper.
/// Filtra por tenant: un id de otro tenant no aparece.
/// </summary>
public interface IApiMessageStates
{
    Task<IReadOnlyDictionary<Guid, ApiMessageState>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> ListFailedIdsAsync(
        Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken);
}

public enum ApiKeyRequestResult
{
    Success,
    Failure
}

/// <summary>
/// <c>FailedMessageIds</c> son los mensajes que fallaron aunque la llamada diera 2xx (el webhook de
/// Meta llega después): sin ellos «falla» y «éxito» solo mirarían el HTTP.
/// </summary>
public sealed record ApiKeyRequestSearch(
    Guid TenantId,
    Guid? ApiKeyId,
    ApiKeyRequestResult? Result,
    IReadOnlyCollection<Guid> FailedMessageIds,
    string? TraceId,
    DateTimeOffset? OccurredFrom,
    DateTimeOffset? OccurredTo,
    int Page,
    int PageSize);

public interface IApiKeyRequestRepository
{
    /// <summary>Más nueva primero (<c>OccurredAt DESC, Id DESC</c>). El total es el del filtro, no el de la página.</summary>
    Task<(IReadOnlyList<ApiKeyRequest> Items, int Total)> SearchAsync(
        ApiKeyRequestSearch search, CancellationToken cancellationToken);
}
