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
    PaymentProofsChanged
}
