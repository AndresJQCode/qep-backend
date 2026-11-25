namespace Modules.ServiceContracts.Application;

/// <summary>Las acciones que el módulo publica a auditoría (spec cobros-recurrentes §4 y §3.2b).</summary>
public static class ServiceContractsAuditActions
{
    public const string ChargePaid = "billing.charge.paid";

    public const string ChargePaymentUpdated = "billing.charge.payment_updated";

    public const string ServiceCreated = "billing.service.created";

    public const string ServiceUpdated = "billing.service.updated";

    public const string ServicePaused = "billing.service.paused";

    public const string ServiceResumed = "billing.service.resumed";

    public const string ServiceCancelled = "billing.service.cancelled";

    public const string ServiceDeleted = "billing.service.deleted";

    public const string ProfileUpdated = "billing.profile.updated";

    public const string SettingsUpdated = "billing.settings.updated";

    public const string ImportCompleted = "billing.import.completed";

    // Reajuste de precio (spec 2026-11-24 §6.14). La aplicación de un programado en la corrida diaria no audita: queda en el
    // historial de precios del contrato.
    public const string ServicePriceReviewUpdated = "billing.service.price_review_updated";

    public const string ServicePriceScheduled = "billing.service.price_scheduled";

    public const string ServicePriceScheduleCancelled = "billing.service.price_schedule_cancelled";

    public const string ServicePriceKept = "billing.service.price_kept";

    public const string IpcSaved = "billing.ipc.saved";

    /// <summary>Tipos de recurso (<c>resourceType</c>) de esas acciones.</summary>
    public static class Resources
    {
        public const string Charge = "charge";

        public const string Service = "service";

        public const string Profile = "profile";

        public const string Settings = "settings";

        public const string Import = "import";

        /// <summary>El <c>resourceId</c> es el año.</summary>
        public const string IpcRate = "ipc_rate";
    }
}
