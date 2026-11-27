using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Wms.Application;

namespace Bootstrapper;

/// <summary>
/// Storage → Wms (novedades, spec 2026-11-26 §8.4, DA-49): qué ids son evidencia del tenant. Copia de
/// <c>QuotationFileLookup.FindManyAsync</c> sin mirar el estado: una foto tomada sin conexión llega a la novedad antes de terminar de
/// subir, así que <c>PendingUpload</c> también vale. <c>ListByIdsAsync</c> no filtra por tenant: el filtro va acá, sobre lo que llegó
/// en el mismo viaje, y otro tenant o otro tipo de dueño no viene, como un archivo que no existe.
/// </summary>
internal sealed class WmsEvidenceLookup(IFileResourceRepository repository) : IWmsEvidenceLookup
{
    public async Task<IReadOnlySet<Guid>> FindEvidenceAsync(
        Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        if (fileIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var resources = await repository.ListByIdsAsync(
            [.. fileIds.Distinct().Select(fileId => new FileResourceId(fileId))], cancellationToken);
        return resources
            .Where(resource => resource.TenantId == tenantId && resource.OwnerType == FileOwnerType.WmsEvidence)
            .Select(resource => resource.Id.Value)
            .ToHashSet();
    }
}
