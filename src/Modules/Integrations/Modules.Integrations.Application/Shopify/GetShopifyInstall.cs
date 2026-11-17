using BuildingBlocks.Application;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.Application.Shopify;

/// <summary>Spec 2026-10-15 §4.3 paso 5: la pantalla <c>/shopify/link/{installId}</c>. Sólo autenticación: todavía no hay
/// tenant elegido.</summary>
public sealed record GetShopifyInstallQuery(Guid InstallId) : IQuery<ShopifyInstallResponse>;

/// <summary>
/// Todo lo que la pantalla de elección dibuja en una llamada. <see cref="Tenants"/> trae <b>todos</b> los tenants activos
/// del usuario, también los que no puede usar, con las dos banderas: la pantalla explica cada caso («pide permiso», «pide
/// a QCode que active Shopify en…») y, sin las filas que faltan, tendría que adivinar por qué no aparece un tenant que la
/// persona sí conoce. Vacía, no ausente, cuando el usuario no tiene tenants (la pantalla ofrece el registro).
/// </summary>
public sealed record ShopifyInstallResponse(
    string ShopDomain,
    string ShopName,
    DateTimeOffset ExpiresAt,
    Guid? InitiatedTenantId,
    IReadOnlyList<ShopifyInstallTenantResponse> Tenants);

/// <param name="CanLink">El usuario tiene <c>integrations.connection.manage</c> en ese tenant.</param>
/// <param name="ModuleEnabled">El tenant tiene el módulo <c>shopify</c> activo.</param>
/// <param name="ConnectedShops">Spec 2026-11-17 §3: las tiendas Shopify ya conectadas a ese tenant, para que la pantalla diga
/// «ya tiene 1 tienda conectada» y nadie cruce tiendas por error; si la tienda que se instala está en la lista, ofrece «Ir a
/// la configuración». Sólo con <paramref name="CanLink"/> (plan D10: quien no administra integraciones no necesita ver las
/// tiendas del tenant); si no, <c>[]</c>. Siempre presente, nunca <c>null</c>.</param>
public sealed record ShopifyInstallTenantResponse(
    Guid TenantId, string Name, bool CanLink, bool ModuleEnabled, IReadOnlyList<ShopifyConnectedShopResponse> ConnectedShops);

/// <param name="ShopName">El campo <c>shopName</c> de la conexión o, sin él, el nombre de la conexión.</param>
/// <param name="ShopDomain">El campo <c>shopDomain</c>: el <c>*.myshopify.com</c>, que es lo que identifica a la tienda.</param>
public sealed record ShopifyConnectedShopResponse(string ShopName, string ShopDomain);

/// <summary>
/// 404 <c>install_not_found</c> para un id desconocido, vencido o todavía sin canjear (§12: el <c>installId</c> sólo lo
/// revela el canje). Los tenants salen de <see cref="IActiveTenantsQuery"/> —membresía activa y tenant activo, en el orden
/// del selector de sesión— y <c>canLink</c> se resuelve con <see cref="IRolePermissionChecker"/> sobre los roles de esa
/// membresía, en ese tenant: los permisos del request son los del tenant activo de la sesión, no los de los demás.
/// </summary>
public sealed class GetShopifyInstallHandler(
    IShopifyInstallRepository installs,
    IActiveTenantsQuery activeTenants,
    IRolePermissionChecker rolePermissions,
    ITenantModules tenantModules,
    IIntegrationConnectionRepository connections,
    IExecutionContext executionContext,
    IClock clock)
    : IQueryHandler<GetShopifyInstallQuery, ShopifyInstallResponse>
{
    public async Task<ShopifyInstallResponse> HandleAsync(GetShopifyInstallQuery query, CancellationToken cancellationToken)
    {
        var install = await installs.FindAsync(query.InstallId, cancellationToken);
        if (install is null || !install.IsExchanged || clock.UtcNow >= install.ExpiresAt)
        {
            throw ShopifyInstallErrors.InstallNotFound();
        }

        var tenants = new List<ShopifyInstallTenantResponse>();
        foreach (var tenant in await activeTenants.ListActiveTenantsAsync(executionContext.SubjectId, cancellationToken))
        {
            var canLink = await rolePermissions.AnyGrantsAsync(
                new TenantId(tenant.TenantId), tenant.Roles, IntegrationsPermissions.ConnectionManage, cancellationToken);
            // null = tenant fuera de tenancy.tenants (sólo el stub): no se enmascara, como TenantModuleGuard.
            var modules = await tenantModules.FindAsync(tenant.TenantId, cancellationToken);
            tenants.Add(new ShopifyInstallTenantResponse(
                tenant.TenantId,
                tenant.DisplayName,
                canLink,
                modules is null || modules.IsEnabled(TenantModuleKeys.Shopify),
                canLink ? await ConnectedShopsAsync(tenant.TenantId, cancellationToken) : []));
        }

        return new ShopifyInstallResponse(install.ShopDomain, install.ShopName!, install.ExpiresAt, install.InitiatedTenantId, tenants);
    }

    /// <summary>Las conexiones <c>shopify</c> del tenant que tienen dominio, por nombre (ordinal, sin mayúsculas).</summary>
    private async Task<IReadOnlyList<ShopifyConnectedShopResponse>> ConnectedShopsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await connections.ListAsync(tenantId, cancellationToken))
            .Where(connection => string.Equals(connection.ProviderKey, IntegrationProviders.Shopify.Key, StringComparison.Ordinal))
            .Select(connection => (
                Domain: connection.Fields.GetValueOrDefault(ShopifyFieldKeys.ShopDomain),
                Name: connection.Fields.GetValueOrDefault(ShopifyFieldKeys.ShopName) is { Length: > 0 } shopName ? shopName : connection.Name))
            .Where(shop => !string.IsNullOrWhiteSpace(shop.Domain))
            .OrderBy(shop => shop.Name, StringComparer.OrdinalIgnoreCase)
            .Select(shop => new ShopifyConnectedShopResponse(shop.Name, shop.Domain!))
            .ToArray();
}
