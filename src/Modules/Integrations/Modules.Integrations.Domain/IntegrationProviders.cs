using Modules.Tenancy.Domain;

namespace Modules.Integrations.Domain;

public static class ZenviaFieldKeys
{
    public const string ApiToken = "apiToken";
    public const string FromNumber = "fromNumber";
}

/// <summary>Spec 2026-10-09 §6.1. Los dos primeros son de sólo lectura para la pantalla; los otros
/// cuatro son internos (los escribe el handler de Embedded Signup o el probador).</summary>
public static class WhatsAppCloudFieldKeys
{
    public const string DisplayPhoneNumber = "displayPhoneNumber";
    public const string VerifiedName = "verifiedName";
    public const string PhoneNumberId = "phoneNumberId";
    public const string WabaId = "wabaId";
    public const string QualityRating = "qualityRating";
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
        ],
        maxConnections: 5,
        onboarding: ProviderOnboarding.MetaEmbeddedSignup);

    public static readonly IReadOnlyList<IntegrationProvider> All = [Zenvia, WhatsAppCloud];

    /// <summary>Ordinal, como el <c>CHECK</c>: <c>"Zenvia"</c> no es <c>"zenvia"</c>.</summary>
    public static IntegrationProvider? Find(string? key) =>
        key is null
            ? null
            : All.FirstOrDefault(provider => string.Equals(provider.Key, key, StringComparison.Ordinal));

    /// <summary>Como <c>TenantModuleKey.Parse</c>: lanza con una clave desconocida.</summary>
    public static IntegrationProvider Parse(string key) =>
        Find(key) ?? throw new ArgumentException($"'{key}' is not a known integration provider key.", nameof(key));
}
