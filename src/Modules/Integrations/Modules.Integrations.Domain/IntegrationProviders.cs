using Modules.Tenancy.Domain;

namespace Modules.Integrations.Domain;

public static class ZenviaFieldKeys
{
    public const string ApiToken = "apiToken";
    public const string FromNumber = "fromNumber";
    public const string TemplateId = "templateId";
    public const string OrderTemplateId = "orderTemplateId";
}

/// <summary>Spec 2026-10-09 §6.1. Los dos primeros son de sólo lectura para la pantalla; los demás son
/// internos (los escribe el handler de Embedded Signup o el probador). <see cref="RegistrationPin"/> es el
/// PIN de verificación en dos pasos con el que se registró el número: Meta exige el vigente en cada
/// <c>/register</c>, así que sin guardarlo no hay re-registro posible.</summary>
public static class WhatsAppCloudFieldKeys
{
    public const string DisplayPhoneNumber = "displayPhoneNumber";
    public const string VerifiedName = "verifiedName";
    public const string PhoneNumberId = "phoneNumberId";
    public const string WabaId = "wabaId";
    public const string QualityRating = "qualityRating";
    public const string AccessToken = "accessToken";
    public const string RegistrationPin = "registrationPin";
}

/// <summary>Spec 2026-10-14 §6.1. <see cref="Username"/> y <see cref="AccountName"/> son de sólo lectura para la
/// pantalla (los refresca el probador); <see cref="IgUserId"/>, <see cref="TokenExpiresAt"/> y
/// <see cref="AccessToken"/> son internos: los escribe el handler de Instagram Login y el worker de refresco.
/// <see cref="TokenExpiresAt"/> viaja en ISO 8601 (<c>O</c>, UTC): el worker lo lee para saber cuándo refrescar.</summary>
public static class InstagramFieldKeys
{
    public const string Username = "username";
    public const string AccountName = "accountName";
    public const string IgUserId = "igUserId";
    public const string TokenExpiresAt = "tokenExpiresAt";
    public const string AccessToken = "accessToken";
}

/// <summary>Spec 2026-10-15 §4.1. <see cref="ShopDomain"/> y <see cref="ShopName"/> son de sólo lectura para la pantalla
/// (los escribe el enlace y el probador refresca el nombre); <see cref="ShopId"/>, <see cref="Scopes"/>,
/// <see cref="WebhookSubscriptionIds"/> y <see cref="AccessToken"/> son internos. <see cref="Scopes"/> y
/// <see cref="WebhookSubscriptionIds"/> viajan separados por coma; los ids de las suscripciones los escribe y los repone
/// Integrations (enlace y probador, D-S19).</summary>
public static class ShopifyFieldKeys
{
    public const string ShopDomain = "shopDomain";
    public const string ShopName = "shopName";
    public const string ShopId = "shopId";
    public const string Scopes = "scopes";
    public const string WebhookSubscriptionIds = "webhookSubscriptionIds";
    public const string AccessToken = "accessToken";
}

/// <summary>
/// La lista cerrada de proveedores, como <see cref="TenantModuleKeys.All"/> (spec 2026-10-08,
/// «Catálogo»). Cada proveedor entra con el spec de su consumidor, porque sin módulo consumidor nunca
/// sería visible (decisión 4): Zenvia con <c>quotations</c>, WhatsApp Business (Meta) con
/// <c>messaging</c> (spec 2026-10-09), Shopify con <c>shopify</c> (spec 2026-10-15).
/// </summary>
public static class IntegrationProviders
{
    /// <summary>D1: tope de conexiones por tenant y proveedor.</summary>
    public const int DefaultMaxConnections = 20;

    // El id de una plantilla de Zenvia es un UUID (`d0e79149-ed61-4784-9883-d86807f46ce5`).
    private const string ZenviaTemplateIdPattern =
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z";

    private const string ZenviaTemplateIdMessage =
        "Copia el ID de la plantilla tal como aparece en Zenvia (formato xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).";

    /// <summary>
    /// La cuenta de Zenvia, el número emisor y sus plantillas. Las plantillas sí son campos de la conexión
    /// (spec 2026-10-21 D11, que revisa la D6 del 2026-10-08): en Zenvia una plantilla va atada al número que
    /// la envía. La de pedido es opcional: sin ella el pedido responde <c>order.whatsapp.not_configured</c>.
    /// Los patrones de token y número son los de <c>UpdateWhatsAppSettingsValidator</c> de <c>6612298</c>:
    /// token en ASCII visible y número E.164 sin «+».
    /// </summary>
    public static readonly IntegrationProvider Zenvia = new(
        "zenvia",
        "Zenvia (WhatsApp)",
        IntegrationCategory.Messaging,
        [TenantModuleKeys.Quotations],
        [
            new FieldDefinition(
                ZenviaFieldKeys.ApiToken,
                "API token",
                FieldKind.Secret,
                required: true,
                maxLength: 512,
                pattern: @"^[\x21-\x7E]+\z",
                invalidMessage: "La clave sólo puede tener letras, números y símbolos, sin espacios: vuelve a copiarla de Zenvia."),
            new FieldDefinition(
                ZenviaFieldKeys.FromNumber,
                "Número emisor",
                FieldKind.Phone,
                required: true,
                maxLength: 15,
                pattern: @"^[0-9]{10,15}\z",
                invalidMessage: "Escribe el número con indicativo de país, sólo dígitos y sin '+' (entre 10 y 15)."),
            new FieldDefinition(
                ZenviaFieldKeys.TemplateId,
                "ID de la plantilla de cotización",
                FieldKind.Text,
                required: true,
                maxLength: 36,
                pattern: ZenviaTemplateIdPattern,
                invalidMessage: ZenviaTemplateIdMessage),
            new FieldDefinition(
                ZenviaFieldKeys.OrderTemplateId,
                "ID de la plantilla de pedido",
                FieldKind.Text,
                required: false,
                maxLength: 36,
                pattern: ZenviaTemplateIdPattern,
                invalidMessage: ZenviaTemplateIdMessage),
        ],
        DefaultMaxConnections,
        // Plan 2026-10-21: el operador habilita Zenvia por tenant; tener quotations no alcanza.
        requiresTenantEnablement: true);

    /// <summary>Spec 2026-10-09 §6.1: WhatsApp Business (Meta). Lo consume <c>messaging</c>; tope 5 por
    /// tenant. <c>displayPhoneNumber</c> no lleva el patrón E.164 de Zenvia: Meta lo devuelve formateado
    /// («+57 300 123 4567») y se guarda tal cual. El <c>accessToken</c> es un secreto interno: nunca lo
    /// escribe una persona.</summary>
    public static readonly IntegrationProvider WhatsAppCloud = new(
        "whatsapp-cloud",
        "WhatsApp Business (Meta)",
        IntegrationCategory.Messaging,
        [TenantModuleKeys.Messaging],
        [
            new FieldDefinition(
                WhatsAppCloudFieldKeys.DisplayPhoneNumber, "Número", FieldKind.Phone,
                required: false, maxLength: 32, pattern: null,
                invalidMessage: "El número que devolvió Meta no tiene una forma válida."),
            new FieldDefinition(
                WhatsAppCloudFieldKeys.VerifiedName, "Nombre verificado", FieldKind.Text,
                required: false, maxLength: 512, pattern: null,
                invalidMessage: "El nombre verificado que devolvió Meta no tiene una forma válida."),
            new FieldDefinition(
                WhatsAppCloudFieldKeys.PhoneNumberId, "Id del número en Meta", FieldKind.Text,
                required: false, maxLength: 32, pattern: @"^[0-9]{1,32}\z",
                invalidMessage: "El id del número tiene que ser numérico.", isInternal: true),
            new FieldDefinition(
                WhatsAppCloudFieldKeys.WabaId, "Id de la cuenta de WhatsApp Business", FieldKind.Text,
                required: false, maxLength: 32, pattern: @"^[0-9]{1,32}\z",
                invalidMessage: "El id de la cuenta tiene que ser numérico.", isInternal: true),
            new FieldDefinition(
                WhatsAppCloudFieldKeys.QualityRating, "Calidad del número", FieldKind.Text,
                required: false, maxLength: 16, pattern: null,
                invalidMessage: "La calidad que devolvió Meta no tiene una forma válida.", isInternal: true),
            new FieldDefinition(
                WhatsAppCloudFieldKeys.AccessToken, "Token de acceso", FieldKind.Secret,
                required: false, maxLength: 2048, pattern: null,
                invalidMessage: "El token que devolvió Meta no tiene una forma válida.", isInternal: true),
            new FieldDefinition(
                WhatsAppCloudFieldKeys.RegistrationPin, "PIN de verificación en dos pasos", FieldKind.Secret,
                required: false, maxLength: 6, pattern: @"^[0-9]{6}\z",
                invalidMessage: "El PIN tiene que ser de seis dígitos.", isInternal: true),
        ],
        maxConnections: 5,
        onboarding: ProviderOnboarding.MetaEmbeddedSignup);

    /// <summary>Spec 2026-10-14 §6.1: Instagram (Meta), con Instagram Login. Lo consume <c>messaging</c>; tope 5 por
    /// tenant, como <see cref="WhatsAppCloud"/>. El id de la cuenta es numérico (el IGSID del negocio); el token es
    /// un secreto interno que nunca escribe una persona.</summary>
    public static readonly IntegrationProvider Instagram = new(
        "instagram",
        "Instagram (Meta)",
        IntegrationCategory.Messaging,
        [TenantModuleKeys.Messaging],
        [
            new FieldDefinition(
                InstagramFieldKeys.Username, "Usuario", FieldKind.Text,
                required: false, maxLength: 64, pattern: null,
                invalidMessage: "El usuario que devolvió Meta no tiene una forma válida."),
            new FieldDefinition(
                InstagramFieldKeys.AccountName, "Nombre de la cuenta", FieldKind.Text,
                required: false, maxLength: 256, pattern: null,
                invalidMessage: "El nombre que devolvió Meta no tiene una forma válida."),
            new FieldDefinition(
                InstagramFieldKeys.IgUserId, "Id de la cuenta en Meta", FieldKind.Text,
                required: false, maxLength: 64, pattern: @"^[0-9]{1,64}\z",
                invalidMessage: "El id de la cuenta tiene que ser numérico.", isInternal: true),
            new FieldDefinition(
                InstagramFieldKeys.TokenExpiresAt, "Vencimiento del token", FieldKind.Text,
                required: false, maxLength: 40, pattern: null,
                invalidMessage: "El vencimiento del token no tiene una forma válida.", isInternal: true),
            new FieldDefinition(
                InstagramFieldKeys.AccessToken, "Token de acceso", FieldKind.Secret,
                required: false, maxLength: 2048, pattern: null,
                invalidMessage: "El token que devolvió Meta no tiene una forma válida.", isInternal: true),
        ],
        maxConnections: 5,
        onboarding: ProviderOnboarding.InstagramLogin);

    /// <summary>Spec 2026-10-15 §4.1: una tienda Shopify, conectada por OAuth. Lo consume el módulo <c>shopify</c>; tope
    /// <see cref="DefaultMaxConnections"/> por tenant. El dominio es el <c>*.myshopify.com</c> que devuelve Shopify y es la
    /// clave de la ruta del webhook. El token offline de la Admin API es un secreto interno que nunca escribe una
    /// persona.</summary>
    public static readonly IntegrationProvider Shopify = new(
        "shopify",
        "Shopify",
        IntegrationCategory.Commerce,
        [TenantModuleKeys.Shopify],
        [
            new FieldDefinition(
                // El tope es el de la ruta: el dominio es su external_id, y uno más largo no se podría enrutar.
                ShopifyFieldKeys.ShopDomain, "Dominio de la tienda", FieldKind.Text,
                required: false, maxLength: IntegrationConnectionRoute.ExternalIdMaxLength, pattern: null,
                invalidMessage: "El dominio que devolvió Shopify no tiene una forma válida."),
            new FieldDefinition(
                ShopifyFieldKeys.ShopName, "Nombre de la tienda", FieldKind.Text,
                required: false, maxLength: 255, pattern: null,
                invalidMessage: "El nombre que devolvió Shopify no tiene una forma válida."),
            new FieldDefinition(
                ShopifyFieldKeys.ShopId, "Id de la tienda en Shopify", FieldKind.Text,
                required: false, maxLength: 64, pattern: @"^[0-9]{1,64}\z",
                invalidMessage: "El id de la tienda tiene que ser numérico.", isInternal: true),
            new FieldDefinition(
                ShopifyFieldKeys.Scopes, "Permisos concedidos", FieldKind.Text,
                required: false, maxLength: 1024, pattern: null,
                invalidMessage: "Los permisos que devolvió Shopify no tienen una forma válida.", isInternal: true),
            new FieldDefinition(
                ShopifyFieldKeys.WebhookSubscriptionIds, "Suscripciones de webhooks", FieldKind.Text,
                required: false, maxLength: 2048, pattern: null,
                invalidMessage: "Las suscripciones que devolvió Shopify no tienen una forma válida.", isInternal: true),
            new FieldDefinition(
                ShopifyFieldKeys.AccessToken, "Token de acceso", FieldKind.Secret,
                required: false, maxLength: 2048, pattern: null,
                invalidMessage: "El token que devolvió Shopify no tiene una forma válida.", isInternal: true),
        ],
        DefaultMaxConnections,
        onboarding: ProviderOnboarding.ShopifyOAuth);

    public static readonly IReadOnlyList<IntegrationProvider> All = [Zenvia, WhatsAppCloud, Instagram, Shopify];

    /// <summary>Ordinal, como el <c>CHECK</c>: <c>"Zenvia"</c> no es <c>"zenvia"</c>.</summary>
    public static IntegrationProvider? Find(string? key) =>
        key is null
            ? null
            : All.FirstOrDefault(provider => string.Equals(provider.Key, key, StringComparison.Ordinal));

    /// <summary>Como <c>TenantModuleKey.Parse</c>: lanza con una clave desconocida.</summary>
    public static IntegrationProvider Parse(string key) =>
        Find(key) ?? throw new ArgumentException($"'{key}' is not a known integration provider key.", nameof(key));
}
