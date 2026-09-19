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
internal sealed class QuotationTenantLogoLookup(
    ITenantDirectory tenantDirectory,
    IFileResourceRepository repository,
    IObjectStorage objectStorage) : IQuotationLogoLookup
{
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

    public Task<byte[]> ReadAsync(QuotationLogoRef reference, CancellationToken cancellationToken) =>
        objectStorage.DownloadAsync(reference.StorageKey, cancellationToken);
}
