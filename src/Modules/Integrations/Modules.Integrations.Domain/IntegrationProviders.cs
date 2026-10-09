using Modules.Tenancy.Domain;

namespace Modules.Integrations.Domain;

public static class ZenviaFieldKeys
{
    public const string ApiToken = "apiToken";
    public const string FromNumber = "fromNumber";
}

/// <summary>
/// La lista cerrada de proveedores, como <see cref="TenantModuleKeys.All"/> (spec 2026-10-08,
/// «Catálogo»). La versión 1 tiene uno: OpenAI, Meta y transportadoras entran con el spec de su
/// consumidor, porque sin módulo consumidor nunca serían visibles (decisión 4).
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
                pattern: @"^[\x21-\x7E]+$",
                invalidMessage: "La clave sólo puede tener letras, números y símbolos, sin espacios: vuelve a copiarla de Zenvia."),
            new FieldDefinition(
                ZenviaFieldKeys.FromNumber,
                "Número emisor",
                FieldKind.Phone,
                required: true,
                maxLength: 15,
                pattern: "^[0-9]{10,15}$",
                invalidMessage: "Escribe el número con indicativo de país, sólo dígitos y sin '+' (entre 10 y 15)."),
        ],
        DefaultMaxConnections);

    public static readonly IReadOnlyList<IntegrationProvider> All = [Zenvia];

    /// <summary>Ordinal, como el <c>CHECK</c>: <c>"Zenvia"</c> no es <c>"zenvia"</c>.</summary>
    public static IntegrationProvider? Find(string? key) =>
        key is null
            ? null
            : All.FirstOrDefault(provider => string.Equals(provider.Key, key, StringComparison.Ordinal));

    /// <summary>Como <c>TenantModuleKey.Parse</c>: lanza con una clave desconocida.</summary>
    public static IntegrationProvider Parse(string key) =>
        Find(key) ?? throw new ArgumentException($"'{key}' is not a known integration provider key.", nameof(key));
}
