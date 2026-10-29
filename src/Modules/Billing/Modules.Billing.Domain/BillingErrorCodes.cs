namespace Modules.Billing.Domain;

/// <summary>
/// Códigos estables de error del módulo (spec cobros-recurrentes §3 y §4). El mapeo a HTTP vive
/// en <c>ApiExceptionHandler</c>; acá sólo el contrato.
/// </summary>
public static class BillingErrorCodes
{
    public const string ServiceNotFound = "billing.service.not_found";

    public const string ChargeNotFound = "billing.charge.not_found";

    public const string ChargeNotPending = "billing.charge.not_pending";

    public const string ChargeNotPaid = "billing.charge.not_paid";

    public const string PaymentDateInFuture = "billing.payment.date_in_future";

    public const string PaymentNoteTooLong = "billing.payment.note_too_long";

    public const string ServiceInvalidTransition = "billing.service.invalid_transition";

    public const string ServiceCancelled = "billing.service.cancelled";

    public const string ServiceInvalid = "billing.service.invalid";

    public const string ProfileMissing = "billing.profile.missing";

    public const string ProfileNotFound = "billing.profile.not_found";

    public const string ProfileAssigneeInvalid = "billing.profile.assignee_invalid";

    public const string ProfileWhatsAppRequiresPhone = "billing.profile.whatsapp_requires_phone";

    public const string ProfileEmailRequiresAddress = "billing.profile.email_requires_address";

    public const string CustomerNotFound = "billing.customer.not_found";

    public const string CustomerInactive = "billing.customer.inactive";

    public const string CurrencyUnknown = "billing.currency.unknown";

    public const string SettingsConnectionUnknown = "billing.settings.connection_unknown";
}
