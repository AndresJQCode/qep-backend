namespace Modules.Quotations.Domain;

public enum OrderHistoryEventType
{
    Created,
    ItemsChanged,
    PartiesChanged,
    PricingChanged,
    NotesChanged,
    Approved,
    Cancelled,
    Invoiced,
    InvoicingReverted,
    PaymentProofsChanged,

    /// <summary>El PDF del pedido se le mandó al cliente por WhatsApp. Al final a propósito: la columna
    /// guarda el nombre (<c>HasConversion&lt;string&gt;()</c>), pero el orden no se reescribe.</summary>
    Sent,

    // Spec 2026-11-10: los ejes del pedido. Al final, en este orden; Approved se queda por las filas viejas.
    PaidMarked,
    PaidUnmarked,
    Prepared,
    PreparationUndone,
    Archived,
    Unarchived,
    PaymentStatusRecalculated,

    /// <summary>Spec ReteICA por empresa §7.8: cambió el porcentaje de una retención municipal (editado, restablecido o
    /// descartado por una re-resolución) o se quitó una porque el cliente no es agente ahí. Al final, como los demás.</summary>
    WithholdingRateEdited,

    /// <summary>Spec 2026-11-15 §4: un asesor cambió o quitó la cuenta de comprador del pedido de la tienda. Sólo la decisión del
    /// asesor escribe esta entrada: los enlaces automáticos (checkout, inicio de sesión, alta diferida) no. Al final, como los
    /// demás.</summary>
    BuyerAccountChanged,
}
