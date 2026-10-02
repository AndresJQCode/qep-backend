namespace Modules.Quotations.Application;

public static class OrdersPermissions
{
    public const string OrderRead = "quotations.order.read";
    public const string OrderManage = "quotations.order.manage";

    /// <summary>Dar el visto bueno a un pedido pendiente. Aparte de <see cref="OrderManage"/>:
    /// quien registra el pedido no es quien lo revisa, así que la asesora no se aprueba a sí
    /// misma.</summary>
    public const string OrderApprove = "quotations.order.approve";

    /// <summary>Anular un pedido (spec 2026-09-16, decisión 5). Aparte de <see cref="OrderManage"/>:
    /// quien edita pedidos no tiene por qué poder deshacer uno aprobado.</summary>
    public const string OrderCancel = "quotations.order.cancel";
}
