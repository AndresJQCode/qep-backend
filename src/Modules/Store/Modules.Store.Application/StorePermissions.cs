namespace Modules.Store.Application;

/// <summary>
/// <see cref="SettingsManage"/> (tres segmentos, module.resource.action) es de este módulo y lo concede sólo <c>admin</c> (owner,
/// 2026-11-03). Los pedidos de la tienda se atienden en Pedidos, con los permisos de Quotations.
/// </summary>
public static class StorePermissions
{
    public const string SettingsManage = "store.settings.manage";
}
