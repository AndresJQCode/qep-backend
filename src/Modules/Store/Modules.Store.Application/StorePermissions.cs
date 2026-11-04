namespace Modules.Store.Application;

/// <summary>
/// Un permiso propio y uno prestado. <see cref="SettingsManage"/> (tres segmentos, module.resource.action) es de este módulo y lo
/// concede sólo <c>admin</c> (owner, 2026-11-03). <see cref="OrderManage"/> repite el nombre de <c>OrdersPermissions.OrderManage</c> de
/// Quotations, como Shopify en <c>ShopifyAuthorization.cs</c>: Store no referencia Quotations (<c>StoreLayerTests</c>), su política ya
/// la registra <c>AddAuthorization</c> y el descubrimiento de permisos usa <c>.Distinct</c>, así que no cuenta dos veces. Si el nombre
/// no coincide con el del módulo dueño la bandeja queda en 403 para todos; la prueba de integración con el permiso real lo amarra.
/// </summary>
public static class StorePermissions
{
    public const string SettingsManage = "store.settings.manage";

    /// <summary>La bandeja de solicitudes es gestión de pedidos (spec §4.4): quien toma un pedido puede tomar uno de la tienda.</summary>
    public const string OrderManage = "quotations.order.manage";
}
