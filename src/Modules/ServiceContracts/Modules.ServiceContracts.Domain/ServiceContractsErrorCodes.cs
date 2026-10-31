namespace Modules.ServiceContracts.Domain;

/// <summary>
/// Códigos estables de error del módulo (spec cobros-recurrentes §3 y §4). El mapeo a HTTP vive
/// en <c>ApiExceptionHandler</c>; acá sólo el contrato.
/// </summary>
public static class ServiceContractsErrorCodes
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

    /// <summary>El precio o la moneda cambian y el cuerpo no trae <c>priceChangeReason</c>: el historial exige la justificación.</summary>
    public const string PriceChangeReasonRequired = "billing.service.price_reason_required";

    /// <summary>The description exceeds <see cref="ServiceContract.DescriptionMaxLength"/>; the SPA maps this code to the field.</summary>
    public const string ServiceDescriptionTooLong = "billing.service.description_too_long";

    public const string ProfileMissing = "billing.profile.missing";

    public const string ProfileNotFound = "billing.profile.not_found";

    public const string ProfileAssigneeInvalid = "billing.profile.assignee_invalid";

    public const string ProfileWhatsAppRequiresPhone = "billing.profile.whatsapp_requires_phone";

    public const string ProfileEmailRequiresAddress = "billing.profile.email_requires_address";

    public const string CustomerNotFound = "billing.customer.not_found";

    public const string CustomerInactive = "billing.customer.inactive";

    public const string CurrencyUnknown = "billing.currency.unknown";

    public const string SettingsConnectionUnknown = "billing.settings.connection_unknown";

    // Importación desde Excel (§3.2b): los del archivo son 422; los de fila viajan en ImportRowErrorDto.
    public const string ImportFileRequired = "billing.import.file_required";

    public const string ImportExtensionInvalid = "billing.import.extension_invalid";

    public const string ImportTooLarge = "billing.import.too_large";

    public const string ImportUnreadable = "billing.import.unreadable";

    public const string ImportTooManyRows = "billing.import.too_many_rows";

    public const string ImportHeaderMismatch = "billing.import.header_mismatch";

    public const string ImportRowCustomerNameRequired = "billing.import.row.customer_name_required";

    public const string ImportRowIdentificationIncomplete = "billing.import.row.identification_incomplete";

    public const string ImportRowIdentificationTypeInvalid = "billing.import.row.identification_type_invalid";

    public const string ImportRowPhoneRequired = "billing.import.row.phone_required";

    public const string ImportRowPhoneInvalid = "billing.import.row.phone_invalid";

    public const string ImportRowEmailInvalid = "billing.import.row.email_invalid";

    public const string ImportRowEmailRequired = "billing.import.row.email_required";

    public const string ImportRowCountryInvalid = "billing.import.row.country_invalid";

    public const string ImportRowServiceNameRequired = "billing.import.row.service_name_required";

    public const string ImportRowPriceInvalid = "billing.import.row.price_invalid";

    public const string ImportRowCurrencyUnknown = "billing.import.row.currency_unknown";

    public const string ImportRowCycleInvalid = "billing.import.row.cycle_invalid";

    public const string ImportRowDueDateInvalid = "billing.import.row.due_date_invalid";

    public const string ImportRowChannelInvalid = "billing.import.row.channel_invalid";

    public const string ImportRowAssigneeRequired = "billing.import.row.assignee_required";

    public const string ImportRowAssigneeInvalid = "billing.import.row.assignee_invalid";

    public const string ImportRowCustomerAmbiguous = "billing.import.row.customer_ambiguous";

    public const string ImportRowCustomerInactive = "billing.import.row.customer_inactive";

    public const string ImportRowDuplicateService = "billing.import.row.duplicate_service";

    public const string ImportRowServiceExists = "billing.import.row.service_exists";

    public const string ImportRowFailed = "billing.import.row.failed";
}
