using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Store.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Tenancy + Storage → Store: nombre, país, estado, formato regional y logo público del tenant. El formato regional sale de
/// <see cref="ITenantDirectory.GetRegionalSettingsAsync"/>, la misma lectura de <c>/auth/me</c>. El logo es la URL pública de
/// <see cref="ITenantLogoStorage"/> sobre la llave que Storage publicó; un logo sin publicar o de otro tenant sale sin URL.
/// </summary>
internal sealed class StoreTenantLookup(
    ITenantDirectory directory,
    IFileResourceRepository files,
    ITenantLogoStorage logoStorage) : IStoreTenantLookup
{
    public async Task<StoreTenantRef?> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var id = new TenantId(tenantId);
        var name = await directory.GetDisplayNameAsync(id, cancellationToken);
        var regional = await directory.GetRegionalSettingsAsync(id, cancellationToken);
        if (name is null || regional is null)
        {
            return null;
        }

        var status = await directory.GetStatusAsync(id, cancellationToken);
        return new StoreTenantRef(
            name,
            regional.Country,
            status == TenantStatus.Active,
            await LogoUrlAsync(id, tenantId, cancellationToken),
            new StoreRegionalFormat(
                regional.TimeZone, regional.DateFormat, regional.NumberFormat, regional.DefaultCurrency, regional.Country));
    }

    private async Task<string?> LogoUrlAsync(TenantId id, Guid tenantId, CancellationToken cancellationToken)
    {
        var fileId = await directory.GetLogoFileIdAsync(id, cancellationToken);
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
