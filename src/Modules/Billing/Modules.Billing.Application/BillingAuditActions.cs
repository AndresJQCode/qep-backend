namespace Modules.Billing.Application;

/// <summary>Las acciones que el módulo publica a auditoría (spec cobros-recurrentes §4 y §3.2b).</summary>
public static class BillingAuditActions
{
    public const string ChargePaid = "billing.charge.paid";

    public const string ChargePaymentUpdated = "billing.charge.payment_updated";

    public const string ServiceCreated = "billing.service.created";

    public const string ServiceUpdated = "billing.service.updated";

    public const string ServicePaused = "billing.service.paused";

    public const string ServiceResumed = "billing.service.resumed";

    public const string ServiceCancelled = "billing.service.cancelled";

    public const string ProfileUpdated = "billing.profile.updated";

    public const string SettingsUpdated = "billing.settings.updated";

    public const string ImportCompleted = "billing.import.completed";

    /// <summary>Tipos de recurso (<c>resourceType</c>) de esas acciones.</summary>
    public static class Resources
    {
        public const string Charge = "charge";

        public const string Service = "service";

        public const string Profile = "profile";

        public const string Settings = "settings";

        public const string Import = "import";
    }
}
