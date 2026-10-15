using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-15 §3.1: el catálogo de motivos de resolución del tenant. Todo método recibe <c>tenantId</c>: el id de otro tenant
/// responde igual que uno inexistente. «Vigente» es <c>deleted_at IS NULL</c> (D-R5). El agregado se escribe con tracking y se guarda
/// con <see cref="IMessagingUnitOfWork"/>, que traduce el índice único del nombre a <c>messaging.resolution_reason.name_taken</c>.
/// </summary>
public interface IResolutionReasonRepository
{
    /// <summary>Con tracking. <c>null</c> si está archivado, es de otro tenant o no existe.</summary>
    Task<ResolutionReason?> FindActiveAsync(Guid tenantId, Guid reasonId, CancellationToken cancellationToken);

    /// <summary>Sin tracking. Compara con <c>lower()</c> de Postgres en los dos lados, la misma expresión del índice único, así que
    /// .NET y la base no pueden discrepar sobre qué es «el mismo nombre». <paramref name="trimmedName"/> ya viene recortado.</summary>
    Task<ResolutionReason?> FindActiveByNameAsync(Guid tenantId, string trimmedName, CancellationToken cancellationToken);

    Task<int> CountActiveAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>D-R10: todos los vigentes, sin paginar, por nombre sin distinguir mayúsculas. Sin tracking.</summary>
    Task<IReadOnlyList<ResolutionReason>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(ResolutionReason reason);
}

/// <summary>Spec 2026-10-15 §3.5: los nombres para el historial y el resumen, <b>incluidos los archivados</b> (D-R5). Un id que no aparece
/// se lee como «sin motivo», no como error.</summary>
public interface IResolutionReasonNames
{
    Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> reasonIds, CancellationToken cancellationToken);
}
