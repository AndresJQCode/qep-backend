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
    PaymentStatusRecalculated
}
