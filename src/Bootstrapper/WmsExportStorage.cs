using System.Security.Cryptography;
using BuildingBlocks.Application;
using Microsoft.Extensions.Options;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure;
using Modules.Wms.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el almacenamiento de objetos de <c>Storage</c> al puerto que <c>wms</c> declara para dejar ahí el Excel de saldos (§24.3, S14).
/// A diferencia de <see cref="SupplierExportStorage"/>, que solo manda un enlace prefirmado por correo, el export de WMS queda registrado
/// como <see cref="FileResource"/> de dueño <see cref="FileOwnerType.WmsExport"/>: el <c>fileId</c> que recibe la pantalla se descarga por
/// <c>POST /files/{id}/download-url</c>, y <see cref="FileOwnerModules"/> lo oculta y lo bloquea con el módulo <c>wms</c> apagado, y listarlo o descargarlo exige además <c>wms.stock.export</c>
/// (<see cref="FileOwnerModules.RequiredPermissionByOwnerType"/>).
///
/// El objeto vive bajo <c>exports/</c>, cuya regla de lifecycle del bucket lo borra a los dos días (README «Reportes exportados»): por eso
/// Storage solo emite el enlace durante <see cref="FileResource.ExportDownloadWindow"/> y la fila vencida responde
/// <c>storage.file.expired</c> en vez de un enlace muerto. La fila es del esquema de Storage y se guarda con su propia unidad de trabajo,
/// antes que el evento y la auditoría de Wms: si el guardado de Wms falla queda una fila sin correo, que vence sola; nunca un correo con
/// un archivo que no se puede descargar.
/// </summary>
internal sealed class WmsExportStorage(
    IObjectStorage objectStorage,
    IOptions<StorageOptions> options,
    IFileResourceRepository files,
    IStorageUnitOfWork storageUnitOfWork,
    IClock clock)
    : IWmsExportStorage
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const string ExportPrefix = "exports";

    public async Task<WmsExportUpload> UploadAsync(
        Guid tenantId, Guid fileId, string fileName, byte[] content, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        // El fileId (v7) es también el nombre del objeto: evita que dos exportaciones del mismo tenant se pisen y que se pueda adivinar.
        var key = $"{ExportPrefix}/tenants/{tenantId:N}/wms/{fileId:N}.xlsx";
        await objectStorage.UploadAsync(key, content, ExcelContentType, cancellationToken);

        // Lo genera el servidor y ya está en el bucket: no hay subida del cliente ni nada que escanear, así que el recurso nace Available.
        var resource = FileResource.CreatePendingUpload(
            new FileResourceId(fileId), tenantId, tenantId, FileOwnerType.WmsExport, fileName, ExcelContentType, content.LongLength, key, now);
        resource.CompleteUpload(Convert.ToHexStringLower(SHA256.HashData(content)), content.LongLength, now);
        resource.MarkClean(now);
        files.Add(resource);
        await storageUnitOfWork.SaveChangesAsync(cancellationToken);

        var expiry = TimeSpan.FromHours(options.Value.ExportUrlHours);
        var url = await objectStorage.CreatePresignedDownloadUrlAsync(key, expiry, fileName, cancellationToken);

        // `AbsoluteUri`, nunca `ToString()`: este último desescapa el query string y rompe la firma para cualquier cliente HTTP estricto.
        return new WmsExportUpload(url.AbsoluteUri, now.Add(expiry));
    }
}
