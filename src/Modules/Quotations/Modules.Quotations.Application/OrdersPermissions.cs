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

    /// <summary>Marcar un pedido aprobado como facturado y revertir esa marca (spec 2026-10-05,
    /// decisión 5). Un solo permiso para las dos cosas: con uno aparte para revertir, facturación
    /// dependería de un admin para corregir su propio error.</summary>
    public const string OrderInvoice = "quotations.order.invoice";
}
