using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Support.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Fase 4b (H5, owner 2026-11-16, Q-6): el membrete de la respuesta formal. La empresa sale de las versiones del formulario de Soporte
/// (la del ticket y, sin empresa ahí, la vigente) y de Companies por <see cref="ISupportOrganizationDirectory"/>, que filtra por tenant;
/// el logo es el del tenant (Tenancy y Storage), leído como lo lee <see cref="QuotationTenantLogoLookup"/>. Vive en el composition root
/// porque Soporte no referencia Companies, Tenancy.Infrastructure ni Storage.
/// </summary>
internal sealed partial class SupportLetterhead(
    ISupportFormRepository forms,
    ISupportOrganizationDirectory organizations,
    ITenantDirectory tenantDirectory,
    IFileResourceRepository files,
    IObjectStorage objectStorage,
    ILogger<SupportLetterhead> logger) : ISupportLetterhead
{
    // Sólo PNG y JPEG: qcode-pdf rechaza WebP con un 422 (verificado contra el servicio), y una carta sin logo es mejor que ninguna
    // carta. Por eso no es la tabla de TenantLogoStorage, que sí admite WebP.
    private static readonly Dictionary<string, string> LogoNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = "logo.png",
        ["image/jpeg"] = "logo.jpg",
    };

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Could not read the tenant logo (file {FileId}); the formal reply letterhead goes out without it.")]
    private static partial void LogLogoUnreadable(ILogger logger, Guid fileId, Exception exception);

    public async Task<SupportLetterheadData?> FindAsync(Guid tenantId, Guid? formVersionId, CancellationToken cancellationToken)
    {
        var companyId = await CompanyOfAsync(tenantId, formVersionId, cancellationToken);
        if (companyId is null)
        {
            return null;
        }

        // La empresa que eligió el formulario, activa o no: es la que respondió en el portal. De otro tenant, el directorio da null.
        var company = await organizations.FindAsync(tenantId, companyId.Value, cancellationToken);
        if (company is null)
        {
            return null;
        }

        return new SupportLetterheadData(
            company.Name,
            company.DocumentTypeCode,
            company.DocumentNumber,
            company.Address,
            company.CityName,
            company.Email,
            company.Phone,
            await LogoAsync(tenantId, cancellationToken));
    }

    private async Task<Guid?> CompanyOfAsync(Guid tenantId, Guid? formVersionId, CancellationToken cancellationToken)
    {
        if (formVersionId is { } id
            && await forms.FindAsync(tenantId, id, cancellationToken) is { OrganizationCompanyId: { } ticketCompany })
        {
            return ticketCompany;
        }

        return (await forms.CurrentAsync(tenantId, cancellationToken))?.OrganizationCompanyId;
    }

    private async Task<SupportPdfLogo?> LogoAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var fileId = await tenantDirectory.GetLogoFileIdAsync(new TenantId(tenantId), cancellationToken);
        if (fileId is null)
        {
            return null;
        }

        var resource = await files.GetAsync(new FileResourceId(fileId.Value), cancellationToken);
        if (resource is null
            || resource.TenantId != tenantId
            || resource.Status is not FileResourceStatus.Available
            || !LogoNames.TryGetValue(resource.MimeType, out var name))
        {
            // Sin archivo disponible, de otro tenant (se revalida en la frontera) o WebP: el membrete sale sin logo, no falla.
            return null;
        }

        try
        {
            return new SupportPdfLogo(name, await objectStorage.DownloadAsync(resource.StorageKey, cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // R2 caído u objeto faltante: la respuesta formal no se bloquea por el logo.
            LogLogoUnreadable(logger, resource.Id.Value, exception);
            return null;
        }
    }
}
