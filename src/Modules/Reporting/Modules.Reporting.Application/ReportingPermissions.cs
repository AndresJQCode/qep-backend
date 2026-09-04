namespace Modules.Reporting.Application;

/// <summary>
/// Tres segmentos, <c>module.resource.action</c>, igual que el resto de los permisos ya
/// registrados. El recurso va en **singular** —<c>quotation</c>, <c>price_change</c>,
/// <c>customer</c>— siguiendo a <c>catalog.product.read</c> y <c>customers.customer.read</c>.
/// <c>sales</c> es la excepción y va en plural, porque así lo fija el contrato de API con el
/// frontend.
///
/// Los cuatro son de sólo lectura: este módulo no escribe nada. La partición no es por reporte
/// sino por sensibilidad del dato — ventas y cotizaciones son el trabajo diario de la asesora,
/// mientras que el histórico de precios y el padrón completo de clientes son de administración.
///
/// **Cada uno necesita sus TRES registros** en <c>QepServiceCollectionExtensions</c>: el
/// <c>PermissionDefinition</c> del catálogo, la lista del <c>RoleDefinition</c> que lo otorga, y
/// el <c>AddPolicy</c>. Sin la política, <c>RequireAuthorization</c> no resuelve y el síntoma es
/// **500, no 403**.
/// </summary>
public static class ReportingPermissions
{
    public const string SalesRead = "reporting.sales.read";
    public const string QuotationRead = "reporting.quotation.read";
    public const string PriceChangeRead = "reporting.price_change.read";
    public const string CustomerRead = "reporting.customer.read";
}
