namespace Modules.Wms.Application;

/// <summary>Wms → Storage (spec 2026-11-26 §8.4, DA-49): cuáles de los archivos que una novedad nombra son evidencia del tenant. Wms
/// no referencia Storage; el composition root lo adapta.</summary>
public interface IWmsEvidenceLookup
{
    /// <summary>Los ids que son archivos <c>WmsEvidence</c> del tenant, en cualquier estado (pueden estar subiendo todavía, DA-49). Los
    /// de otro tenant o de otro tipo de dueño no vienen, como uno que no existe.</summary>
    Task<IReadOnlySet<Guid>> FindEvidenceAsync(
        Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken);
}
