using Modules.Wms.Domain.Control;

namespace Modules.Wms.Application;

/// <summary>Los previews de la carga inicial viven en la base y no en memoria: varias réplicas no comparten memoria, y el commit puede
/// caer en otra distinta de la que armó el preview.</summary>
public interface IImportPreviewStore
{
    /// <summary>Cuánto vive un preview desde que se crea (<c>Wms:Workers:PreviewRetentionMinutes</c>).</summary>
    TimeSpan Lifetime { get; }

    void Add(ImportPreview preview);

    /// <summary>Un preview se consume al commitearse: otra clave de idempotencia sobre el mismo preview no carga el saldo dos veces.</summary>
    void Remove(ImportPreview preview);

    Task<ImportPreview?> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
