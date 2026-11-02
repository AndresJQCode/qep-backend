using Microsoft.Extensions.Options;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure;
using Modules.Wms.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el almacenamiento de objetos de <c>Storage</c> al puerto que <c>wms</c> declara para dejar ahí el Excel de saldos. Copia de
/// <see cref="SupplierExportStorage"/>: ningún módulo de negocio referencia al otro, y el composition root es el único lugar donde ese
/// acoplamiento es legítimo.
///
/// El archivo no se registra como <c>FileResource</c> (es un artefacto temporal que se descarga una vez y se vence, y su ciclo de vida
/// lo maneja la regla del bucket bajo el prefijo <c>exports/</c>), pero su dueño lógico es <see cref="FileOwnerType.WmsExport"/>, mapeado
/// al módulo <c>wms</c> en <see cref="FileOwnerModules"/> (S14): el día que se registre, el módulo apagado ya lo oculta.
/// </summary>
internal sealed class WmsExportStorage(IObjectStorage objectStorage, IOptions<StorageOptions> options) : IWmsExportStorage
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const string ExportPrefix = "exports";

    public async Task<WmsExportUpload> UploadAsync(
        Guid tenantId, Guid fileId, string fileName, byte[] content, CancellationToken cancellationToken)
    {
        // El fileId (v7) evita que dos exportaciones del mismo tenant se pisen y que el nombre del objeto se pueda adivinar.
        var key = $"{ExportPrefix}/tenants/{tenantId:N}/wms/{fileId:N}.xlsx";

        await objectStorage.UploadAsync(key, content, ExcelContentType, cancellationToken);

        var expiry = TimeSpan.FromHours(options.Value.ExportUrlHours);
        var url = await objectStorage.CreatePresignedDownloadUrlAsync(key, expiry, fileName, cancellationToken);

        // `AbsoluteUri`, nunca `ToString()`: este último desescapa el query string y rompe la firma para cualquier cliente HTTP estricto.
        return new WmsExportUpload(url.AbsoluteUri, DateTimeOffset.UtcNow.Add(expiry));
    }
}
