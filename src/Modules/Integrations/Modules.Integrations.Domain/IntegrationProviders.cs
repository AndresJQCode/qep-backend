using Modules.Tenancy.Domain;

namespace Modules.Integrations.Domain;

public static class ZenviaFieldKeys
{
    public const string ApiToken = "apiToken";
    public const string FromNumber = "fromNumber";
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

/// <summary>
/// La lista cerrada de proveedores, como <see cref="TenantModuleKeys.All"/> (spec 2026-10-08,
/// «Catálogo»). Cada proveedor entra con el spec de su consumidor, porque sin módulo consumidor nunca
/// sería visible (decisión 4): Zenvia con <c>quotations</c>, WhatsApp Business (Meta) con
/// <c>messaging</c> (spec 2026-10-09).
/// </summary>
public static class IntegrationProviders
{
    /// <summary>D1: tope de conexiones por tenant y proveedor.</summary>
    public const int DefaultMaxConnections = 20;

    /// <summary>
    /// La cuenta de Zenvia y el número emisor. La plantilla de cotización no es campo de la conexión
    /// (D6): es del consumidor. Los patrones son los de <c>UpdateWhatsAppSettingsValidator</c> de
    /// <c>6612298</c>: token en ASCII visible y número E.164 sin «+».
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
        ],
        DefaultMaxConnections);

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

    public static readonly IReadOnlyList<IntegrationProvider> All = [Zenvia, WhatsAppCloud, Instagram];

    /// <summary>Ordinal, como el <c>CHECK</c>: <c>"Zenvia"</c> no es <c>"zenvia"</c>.</summary>
    public static IntegrationProvider? Find(string? key) =>
        key is null
            ? null
            : All.FirstOrDefault(provider => string.Equals(provider.Key, key, StringComparison.Ordinal));

    /// <summary>Como <c>TenantModuleKey.Parse</c>: lanza con una clave desconocida.</summary>
    public static IntegrationProvider Parse(string key) =>
        Find(key) ?? throw new ArgumentException($"'{key}' is not a known integration provider key.", nameof(key));
}
