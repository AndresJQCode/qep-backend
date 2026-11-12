namespace Modules.Quotations.Application;

public static class OrdersPermissions
{
    public const string OrderRead = "quotations.order.read";
    public const string OrderManage = "quotations.order.manage";

    /// <summary>Marcar un pedido como pagado, deshacerlo y quitar comprobantes de un pedido pagado (spec
    /// 2026-11-10 §7; el valor se conserva de cuando era «aprobar»). Aparte de <see cref="OrderManage"/>:
    /// quien carga los comprobantes no es quien verifica la plata, así que la asesora no se marca pagada
    /// a sí misma.</summary>
    public const string OrderApprove = "quotations.order.approve";

    /// <summary>Anular un pedido (spec 2026-09-16, decisión 5). Aparte de <see cref="OrderManage"/>:
    /// quien edita pedidos no tiene por qué poder deshacer uno aprobado.</summary>
    public const string OrderCancel = "quotations.order.cancel";

    /// <summary>Marcar un pedido aprobado como facturado y revertir esa marca (spec 2026-10-05,
    /// decisión 5). Un solo permiso para las dos cosas: con uno aparte para revertir, facturación
    /// dependería de un admin para corregir su propio error.</summary>
    public const string OrderInvoice = "quotations.order.invoice";
}
