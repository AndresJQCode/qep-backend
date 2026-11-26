namespace Modules.Purchases.Application;

/// <summary>Acciones de auditoría del módulo (D-16): una por escritura, sin eventos de outbox en la fase 1.</summary>
public static class PurchasesAuditActions
{
    public const string Created = "purchases.order.created";

    public const string Updated = "purchases.order.updated";

    public const string Deleted = "purchases.order.deleted";

    public const string Sent = "purchases.order.sent";

    public const string Confirmed = "purchases.order.confirmed";

    public const string Received = "purchases.order.received";

    public const string Cancelled = "purchases.order.cancelled";

    public const string EmailSent = "purchases.order.email_sent";

    public const string EmailFailed = "purchases.order.email_failed";

    public static class Resources
    {
        public const string PurchaseOrder = "purchase_order";
    }
}
