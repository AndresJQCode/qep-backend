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
    /// <summary>Graph 133016: diez registros del mismo número en 72 h. Reintentar lo empeora, por eso no es
    /// <see cref="WhatsAppRegistrationFailed"/>.</summary>
    public const string WhatsAppRegistrationLimit = "integrations.whatsapp.registration_limit";
    public const string WhatsAppNumberAlreadyConnected = "integrations.whatsapp.number_already_connected";

    // Spec 2026-10-14 §10, Instagram Login.
    public const string InstagramCodeExchangeFailed = "integrations.instagram.code_exchange_failed";
    /// <summary>La persona desmarcó un permiso en la ventana de Meta: sin los dos no hay bandeja.</summary>
    public const string InstagramPermissionsMissing = "integrations.instagram.permissions_missing";
    public const string InstagramAccountNotProfessional = "integrations.instagram.account_not_professional";
    /// <summary>Una cuenta vive en una sola conexión, de cualquier tenant (índice único de las rutas).</summary>
    public const string InstagramAccountAlreadyConnected = "integrations.instagram.account_already_connected";
    /// <summary>Agregado al implementar (el spec lo deja como V-2): Meta no aceptó suscribir la cuenta al webhook.</summary>
    public const string InstagramSubscriptionFailed = "integrations.instagram.subscription_failed";
    /// <summary>Agregado al implementar: el token canjeó pero <c>/me</c> no respondió; reintentar pide otro code.</summary>
    public const string InstagramAccountReadFailed = "integrations.instagram.account_read_failed";

    // Spec 2026-10-15 §7.1, la instalación de Shopify.
    /// <summary>401: el HMAC de la query de instalación o del callback no coincide.</summary>
    public const string ShopifyHmacInvalid = "integrations.shopify.hmac_invalid";
    /// <summary>422: el dominio no tiene la forma <c>*.myshopify.com</c> (D-S33).</summary>
    public const string ShopifyShopInvalid = "integrations.shopify.shop_invalid";
    /// <summary>404: <c>installId</c> o <c>state</c> desconocido.</summary>
    public const string ShopifyInstallNotFound = "integrations.shopify.install_not_found";
    /// <summary>422: la fila venció (15 min sin canje, 7 días sin enlace).</summary>
    public const string ShopifyInstallExpired = "integrations.shopify.install_expired";
    /// <summary>403: la instalación se inició desde otro tenant.</summary>
    public const string ShopifyInstallTenantMismatch = "integrations.shopify.install_tenant_mismatch";
    /// <summary>422: Shopify no canjeó el <c>code</c> o no describió la tienda con el token recién canjeado; los dos piden
    /// volver a empezar, porque el <c>code</c> ya se gastó.</summary>
    public const string ShopifyCodeExchangeFailed = "integrations.shopify.code_exchange_failed";
    /// <summary>422: lo concedido no contiene <c>Shopify:App:Scopes</c>.</summary>
    public const string ShopifyScopesMissing = "integrations.shopify.scopes_missing";
}

/// <summary>Lo que va a <c>last_failure_code</c> (varchar(64)): el resultado, no el código HTTP.</summary>
public static class ConnectionFailureCodes
{
    public const string CredentialsRejected = "credentials_rejected";
    public const string ProviderUnreachable = "provider_unreachable";
    public const string FieldInvalid = "field_invalid";

    // Spec 2026-10-09 §6.1 y §10.2.
    public const string TokenExpired = "token_expired";
    public const string NumberUnregistered = "number_unregistered";
    public const string AccountDisabled = "account_disabled";

    // Spec 2026-10-15 §4.1 y §4.3.
    /// <summary>La tienda desinstaló la app (<c>app/uninstalled</c>): el token ya no sirve hasta reenlazar.</summary>
    public const string AppUninstalled = "app_uninstalled";
    /// <summary>Shopify no aceptó registrar los webhooks; el probador lo reintenta.</summary>
    public const string WebhooksNotRegistered = "webhooks_not_registered";
}
