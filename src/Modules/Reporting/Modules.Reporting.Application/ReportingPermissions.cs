namespace Modules.Reporting.Application;

/// <summary>
/// Tres segmentos, <c>module.resource.action</c>, igual que el resto de los permisos ya
/// registrados. El recurso va en **singular** —<c>quotation</c>, <c>price_change</c>,
/// <c>customer</c>— siguiendo a <c>catalog.product.read</c> y <c>customers.customer.read</c>.
/// <c>orders</c> es la excepción y va en plural, porque así lo fija el contrato de API con el
/// frontend.
///
/// Todos son de sólo lectura: este módulo no escribe nada. La partición no es por reporte
/// sino por sensibilidad del dato — pedidos y cotizaciones son el trabajo diario de la asesora,
/// mientras que el histórico de precios y el padrón completo de clientes son de administración.
///
/// **Cada uno necesita sus TRES registros** en <c>QepServiceCollectionExtensions</c>: el
/// <c>PermissionDefinition</c> del catálogo, la lista del <c>RoleDefinition</c> que lo otorga, y
/// el <c>AddPolicy</c>. Sin la política, <c>RequireAuthorization</c> no resuelve y el síntoma es
/// **500, no 403**.
/// </summary>
public static class ReportingPermissions
{
    public const string OrdersRead = "reporting.orders.read";
    public const string QuotationRead = "reporting.quotation.read";
    public const string PriceChangeRead = "reporting.price_change.read";
    public const string CustomerRead = "reporting.customer.read";

    /// <summary>
    /// Ver en los reportes de pedidos y de cotizaciones los datos de **todos** los asesores, no
    /// sólo los propios (decisión con el dueño del producto, 2026-09-24).
    ///
    /// No protege un endpoint sino el **alcance** de cuatro que ya protegen <see cref="OrdersRead"/>
    /// y <see cref="QuotationRead"/>: sin él, el handler reemplaza el <c>advisorId</c> que mande
    /// el cliente por el de quien llama (ver <c>ReportingAuthorization.ScopeAdvisorAsync</c>). Es
    /// un permiso y no una pregunta por el nombre del rol porque los roles de un tenant se
    /// configuran: atar la regla a <c>admin</c> la rompería el día que exista un rol de
    /// "gerente comercial".
    ///
    /// El recurso es <c>all_advisors</c> y la acción <c>read</c>, para seguir la forma
    /// <c>module.resource.action</c> de los demás; <c>reporting.advisors.all</c> no tendría verbo.
    /// </summary>
    public const string AllAdvisorsRead = "reporting.all_advisors.read";
}
