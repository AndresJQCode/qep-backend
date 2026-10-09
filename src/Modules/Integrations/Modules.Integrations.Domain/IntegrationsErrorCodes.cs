namespace Modules.Integrations.Domain;

/// <summary>
/// Los códigos del spec 2026-10-08 («Reglas del agregado» y «Códigos de error»). En un solo lugar
/// para que dominio, aplicación, infraestructura y pruebas escriban el mismo texto.
/// </summary>
public static class IntegrationsErrorCodes
{
    public const string NameInvalid = "integrations.connection.name_invalid";
    public const string NameTaken = "integrations.connection.name_taken";
    public const string FieldRequired = "integrations.connection.field_required";
    public const string FieldInvalid = "integrations.connection.field_invalid";
    public const string FieldUnknown = "integrations.connection.field_unknown";
    public const string LimitReached = "integrations.connection.limit_reached";
    public const string NotPaused = "integrations.connection.not_paused";
    public const string NotActive = "integrations.connection.not_active";
    public const string CredentialsRejected = "integrations.connection.credentials_rejected";
    public const string ProviderUnreachable = "integrations.connection.provider_unreachable";
    public const string NotFound = "integrations.connection.not_found";
    public const string SecretProtectionUnavailable = "integrations.secret_protection.unavailable";

    // Spec 2026-10-09 §10.1, los tres de Embedded Signup.
    public const string WhatsAppCodeExchangeFailed = "integrations.whatsapp.code_exchange_failed";
    public const string WhatsAppRegistrationFailed = "integrations.whatsapp.registration_failed";
    public const string WhatsAppNumberAlreadyConnected = "integrations.whatsapp.number_already_connected";
}

/// <summary>Lo que va a <c>last_failure_code</c> (varchar(64)): el resultado, no el código HTTP.</summary>
public static class ConnectionFailureCodes
{
    public const string CredentialsRejected = "credentials_rejected";
    public const string ProviderUnreachable = "provider_unreachable";
    public const string FieldInvalid = "field_invalid";
}
