namespace Modules.Siigo.Application;

/// <summary>
/// Spec 2026-11-18 §5.7: permisos de la integración con Siigo. Cada uno necesita además su <c>PermissionDefinition</c> (con
/// <c>RequiredModules: [siigo]</c>) y su política en <c>QepServiceCollectionExtensions</c>: sin la política el síntoma es 500, no 403.
/// De fábrica, <c>admin</c> lleva los dos y <c>billing</c> sólo <see cref="InvoiceIssue"/>.
/// </summary>
public static class SiigoPermissions
{
    /// <summary>Configurar las conexiones de Siigo (empresa, comprobante, impuestos, vínculos).</summary>
    public const string SettingsManage = "siigo.settings.manage";

    /// <summary>Generar y reintentar la factura electrónica de un pedido en Siigo.</summary>
    public const string InvoiceIssue = "siigo.invoice.issue";

    /// <summary>Ver las llamadas a Siigo que fallaron, con su curl y la respuesta (spec 2026-11-21). De fábrica sólo <c>admin</c>: el cuerpo lleva
    /// datos del cliente. Quien deba verlas (soporte) recibe un rol personalizado con este permiso.</summary>
    public const string CallsRead = "siigo.calls.read";
}
