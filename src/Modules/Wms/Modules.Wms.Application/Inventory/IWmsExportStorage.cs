namespace Modules.Wms.Application;

/// <summary>El enlace prefirmado de un Excel exportado y cuándo vence.</summary>
public sealed record WmsExportUpload(string DownloadUrl, DateTimeOffset ExpiresAt);

/// <summary>Deja el Excel de saldos en el almacenamiento de objetos. Wms no referencia Storage: el adaptador está en Bootstrapper,
/// como el de Suppliers. <c>fileId</c> lo genera el handler para que el evento, la auditoría y la clave del objeto lo compartan.</summary>
public interface IWmsExportStorage
{
    Task<WmsExportUpload> UploadAsync(Guid tenantId, Guid fileId, string fileName, byte[] content, CancellationToken cancellationToken);
}
