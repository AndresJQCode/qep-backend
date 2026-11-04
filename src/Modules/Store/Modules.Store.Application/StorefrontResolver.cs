using BuildingBlocks.Application;
using Modules.Store.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Store.Application;

public sealed record ResolvedStorefront(OnlineStore Store, StoreTenantRef Tenant);

/// <summary>
/// Del host de la ruta a la tienda que lo atiende. Una tienda sin publicar, un host que no es de nadie, un tenant inactivo y un tenant
/// con el módulo apagado dan el mismo 404, con el mismo cuerpo: quien sondea hosts no puede distinguir una tienda en preparación de una
/// que no existe. El módulo falla CERRADO (un tenant sin fila tampoco se sirve), a diferencia del backoffice.
/// </summary>
public sealed class StorefrontResolver(
    IStoreDomainRepository domains,
    IOnlineStoreRepository stores,
    IStoreTenantLookup tenants,
    ITenantModules tenantModules)
{
    public async Task<ResolvedStorefront> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        var hostname = host?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(hostname) || hostname.Length > StoreDomain.HostnameMaxLength)
        {
            throw NotFound();
        }

        var domain = await domains.FindByHostAsync(hostname, cancellationToken) ?? throw NotFound();
        var store = await stores.FindAsync(domain.TenantId, cancellationToken);
        if (store is not { IsPublished: true })
        {
            throw NotFound();
        }

        var tenant = await tenants.GetAsync(domain.TenantId, cancellationToken);
        if (tenant is not { IsActive: true })
        {
            throw NotFound();
        }

        var modules = await tenantModules.FindAsync(domain.TenantId, cancellationToken);
        if (modules is null || !modules.IsEnabled(TenantModuleKeys.Store))
        {
            throw NotFound();
        }

        return new ResolvedStorefront(store, tenant);
    }

    /// <summary>
    /// La moneda en mayúsculas, siempre una de las que vende la tienda. Una moneda que el catálogo usa pero la tienda no ofrece jamás
    /// llega a una lectura: se corta acá, antes de tocar el catálogo.
    /// </summary>
    public static string RequireCurrency(OnlineStore store, string? currency)
    {
        ArgumentNullException.ThrowIfNull(store);

        var code = currency?.Trim().ToUpperInvariant();
        if (code is null || !store.Sells(code))
        {
            throw new StoreDomainException(StoreErrorCodes.PublicCurrencyInvalid, "The currency is not one the store sells.");
        }

        return code;
    }

    // Un solo mensaje para todas las causas: el cuerpo es idéntico salvo el traceId.
    private static ResourceNotFoundException NotFound() =>
        new(StoreErrorCodes.PublicNotFound, "The store was not found.");
}
