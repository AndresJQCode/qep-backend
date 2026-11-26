using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Support.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Tenancy + Storage → Support: el logo público del tenant para el formulario, calco del logo de <see cref="StoreTenantLookup"/>. Es la
/// URL pública de <see cref="ITenantLogoStorage"/> sobre la llave que Storage publicó; un logo sin publicar o de otro tenant sale sin URL.
/// </summary>
internal sealed class SupportTenantBranding(
    ITenantDirectory directory,
    IFileResourceRepository files,
    ITenantLogoStorage logoStorage) : ISupportTenantBranding
{
    public async Task<string?> LogoUrlAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var fileId = await directory.GetLogoFileIdAsync(new TenantId(tenantId), cancellationToken);
        if (fileId is null)
        {
            return null;
        }

        var resource = await files.GetAsync(new FileResourceId(fileId.Value), cancellationToken);
        return resource is { PublicStorageKey: { } key } && resource.TenantId == tenantId
            ? logoStorage.GetUrl(key)
            : null;
    }
}
