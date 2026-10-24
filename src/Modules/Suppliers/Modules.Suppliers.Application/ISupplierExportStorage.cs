namespace Modules.Suppliers.Application;

/// <summary>
/// Deja el Excel exportado en el almacenamiento de objetos y devuelve el enlace de descarga.
/// Puerto en Application con adaptador en el composition root: este módulo no puede referenciar
/// <c>Modules.Storage</c> (<c>SuppliersLayerTests</c> lo verifica).
/// </summary>
public interface ISupplierExportStorage
{
    Task<SupplierExportUpload> UploadAsync(
        Guid tenantId,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken);
}

/// <summary>El enlace y hasta cuándo sirve: el correo tiene que decirle al destinatario cuándo caduca.</summary>
public sealed record SupplierExportUpload(string DownloadUrl, DateTimeOffset ExpiresAt);
