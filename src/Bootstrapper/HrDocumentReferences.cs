using Modules.Hr.Application;
using Modules.Storage.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-26 («PDF del registro eliminado», «Referencias»): le responde a Storage qué PDF de empleados siguen en uso, con el puerto
/// de lectura de Hr (<see cref="IHrDocumentUsage"/>), como <see cref="ChatbotMediaReferences"/> usa <c>IBotMediaReferences</c>. Sólo
/// traduce: nadie lee columnas de Hr por nombre desde fuera. Es la dependencia obligatoria de la purga de Storage; sin este registro el
/// contenedor no se arma y nada se borra.
/// </summary>
internal sealed class HrDocumentReferences(IHrDocumentUsage usage) : IHrDocumentReferences
{
    public Task<IReadOnlySet<Guid>> FindReferencedAsync(
        Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken) =>
        usage.FindUsedAsync(tenantId, fileIds, cancellationToken);
}
