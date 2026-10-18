using BuildingBlocks.Application;
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
public sealed record ShopifyInstallTenantResponse(Guid TenantId, string Name, bool CanLink, bool ModuleEnabled);

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
                tenant.TenantId, tenant.DisplayName, canLink, modules is null || modules.IsEnabled(TenantModuleKeys.Shopify)));
        }

        return new ShopifyInstallResponse(install.ShopDomain, install.ShopName!, install.ExpiresAt, install.InitiatedTenantId, tenants);
    }
}
