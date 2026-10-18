using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §7 y D-S14: sin permisos nuevos. Shopify no referencia Quotations ni Integrations (ShopifyLayerTests), así que
/// repite acá los nombres de los permisos que usa; sus políticas ya las registra <c>AddAuthorization</c> para esos módulos (sin
/// política el síntoma sería 500, no 403). Un nombre que no coincida con el del módulo dueño deja la ruta en 403 para todos: la
/// prueba de integración con el permiso real lo amarra.
/// </summary>
public static class ShopifyPermissions
{
    /// <summary><c>OrdersPermissions.OrderManage</c> de Quotations: la bandeja y «Sincronizar ahora» son gestión de pedidos.</summary>
    public const string OrderManage = "quotations.order.manage";
}

/// <summary>Copia de <c>MessagingAuthorization</c>: tenant de la ruta distinto del activo, o permiso faltante → 403
/// <c>authorization.denied</c>, nunca 404. Después, el módulo <c>shopify</c> → 403 <c>tenancy.module_not_enabled</c>.</summary>
internal static class ShopifyAuthorization
{
    public const string DeniedCode = "authorization.denied";

    public static async Task EnsureAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, string permission, CancellationToken cancellationToken)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw Denied();
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Shopify, cancellationToken);
    }

    public static RequestForbiddenException Denied() =>
        new(DeniedCode, "The subject cannot perform this Shopify operation for this tenant.");
}
