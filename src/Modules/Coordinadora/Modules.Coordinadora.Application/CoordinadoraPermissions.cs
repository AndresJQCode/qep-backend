namespace Modules.Coordinadora.Application;

/// <summary>
/// Spec 2026-11-21 §5.6: permisos de la integración con Coordinadora. Cada uno necesita además su <c>PermissionDefinition</c> (con
/// <c>RequiredModules: [coordinadora]</c>) y su política en <c>QepServiceCollectionExtensions</c>: sin la política el síntoma es 500, no
/// 403. De fábrica, <c>admin</c> lleva los dos y <c>advisor</c> sólo <see cref="ShipmentManage"/>, como <c>quotations.order.manage</c>.
/// </summary>
public static class CoordinadoraPermissions
{
    /// <summary>Configurar la conexión de Coordinadora: remitente, cuenta, producto y paquete por defecto.</summary>
    public const string SettingsManage = "coordinadora.settings.manage";

    /// <summary>Cotizar, generar, imprimir, seguir, anular y resolver la guía de un pedido.</summary>
    public const string ShipmentManage = "coordinadora.shipment.manage";
}
