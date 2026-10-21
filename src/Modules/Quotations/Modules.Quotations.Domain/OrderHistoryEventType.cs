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
    Sent
}
