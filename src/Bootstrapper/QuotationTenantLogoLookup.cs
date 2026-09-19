using Microsoft.Extensions.Logging;
using Modules.Quotations.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Adapta `Tenancy` (el `LogoFileId` vigente) y `Storage` (el `FileResource`) al puerto que
/// `quotations` declara (decisión 9 del spec 2026-09-19). Mismo criterio que
/// <see cref="QuotationFileLookup"/> entre Quotations y Storage: ningún módulo de negocio
/// referencia al otro, y el composition root es el único lugar legítimo para ese acoplamiento.
/// </summary>
internal sealed partial class QuotationTenantLogoLookup(
    ITenantDirectory tenantDirectory,
    IFileResourceRepository repository,
    IObjectStorage objectStorage,
    ILogger<QuotationTenantLogoLookup> logger) : IQuotationLogoLookup
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No se pudo leer el logo del tenant (archivo {FileId}); el PDF sale sin logo y se reintenta en el próximo export.")]
    private static partial void LogReadFailed(ILogger logger, Guid fileId, Exception exception);

    // Los mismos tres tipos que TenantLogoStorage admite al asignar el logo (sin PDF, a
    // diferencia de PublicPaymentProofPublisher.ExtensionsByMimeType).
    private static readonly Dictionary<string, string> ExtensionsByMimeType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"] = ".png",
            ["image/jpeg"] = ".jpg",
            ["image/webp"] = ".webp",
        };

    public async Task<QuotationLogoRef?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var fileId = await tenantDirectory.GetLogoFileIdAsync(new TenantId(tenantId), cancellationToken);
        if (fileId is null)
        {
            return null;
        }

        var resource = await repository.GetAsync(new FileResourceId(fileId.Value), cancellationToken);
        if (resource is null ||
            resource.Status is not FileResourceStatus.Available ||
            !ExtensionsByMimeType.TryGetValue(resource.MimeType, out var extension))
        {
            // Sin archivo Available (alguien lo borró por Storage) o con un tipo que
            // TenantLogoStorage no debería haber dejado asignar: el PDF sale sin logo, no falla.
            return null;
        }

        return new QuotationLogoRef(resource.Id.Value, resource.StorageKey, extension);
    }

    public async Task<byte[]?> ReadAsync(QuotationLogoRef reference, CancellationToken cancellationToken)
    {
        try
        {
            return await objectStorage.DownloadAsync(reference.StorageKey, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Un logo ilegible (R2 caído, objeto faltante) no puede bloquear el export ni el envío
            // por WhatsApp: el PDF sale sin logo y QuotationPdfProvider no lo registra como
            // impreso, así que el próximo export vuelve a intentarlo.
            LogReadFailed(logger, reference.FileId, exception);
            return null;
        }
    }
}
