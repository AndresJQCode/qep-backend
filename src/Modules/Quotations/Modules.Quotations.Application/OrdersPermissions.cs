namespace Modules.Quotations.Application;

public static class OrdersPermissions
{
    public const string OrderRead = "quotations.order.read";
    public const string OrderManage = "quotations.order.manage";

    /// <summary>Anular un pedido (spec 2026-09-16, decisión 5). Aparte de <see cref="OrderManage"/>:
    /// quien edita pedidos no tiene por qué poder deshacer uno aprobado.</summary>
    public const string OrderCancel = "quotations.order.cancel";
}
