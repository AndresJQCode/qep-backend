using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modules.Integrations.Application.Shopify;

/// <summary>
/// La dirección de facturación de la tienda (<c>shop.billingAddress</c>). <c>CountryCode</c> es el <c>countryCodeV2</c> de
/// Shopify. Nada es secreto, pero <c>Phone</c> es dato personal del comerciante: no va a logs (spec 2026-11-17 §2).
/// </summary>
public sealed record ShopifyShopAddress(
    string? Company,
    string? Address1,
    string? Address2,
    string? City,
    string? Province,
    string? ProvinceCode,
    string? Zip,
    string? CountryCode,
    string? Phone);

/// <summary>
/// Spec 2026-11-17 §2 y plan D1: el perfil de la tienda, que se guarda como el campo interno <c>shopProfile</c> de la
/// conexión (JSON compacto). Lo escriben el enlace y el probador; lo leen la sugerencia de empresa y el BFF del asistente.
/// <c>Email</c>, <c>ContactEmail</c> y el teléfono de la dirección son datos personales: no van a logs, auditoría ni eventos.
/// </summary>
public sealed record ShopifyShopProfile(
    string? Email,
    string? ContactEmail,
    string? CurrencyCode,
    string? IanaTimezone,
    bool? TaxesIncluded,
    ShopifyShopAddress? BillingAddress)
{
    /// <summary>El <c>maxLength</c> del campo <c>shopProfile</c> de la conexión.</summary>
    public const int FieldMaxLength = 4096;

    /// <summary>Lo más largo que se guarda de cada texto.</summary>
    public const int TextMaxLength = 255;

    // Sin escapar lo que no es ASCII: «Medellín» queda legible y no se triplica al medirlo contra FieldMaxLength.
    // El JSON se guarda en la base y nunca se pinta como HTML.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>JSON camelCase compacto, sin los nulos. Cada texto se recorta a <see cref="TextMaxLength"/> y uno vacío se
    /// guarda como ausente. Si aun así no cabe en <see cref="FieldMaxLength"/> (textos llenos de caracteres que JSON
    /// escapa), los recorta más hasta que quepa.</summary>
    public string Serialize()
    {
        for (var limit = TextMaxLength; ; limit /= 2)
        {
            var json = JsonSerializer.Serialize(Normalized(limit), Options);
            if (json.Length <= FieldMaxLength || limit == 0)
            {
                return json;
            }
        }
    }

    /// <summary><c>null</c> si el texto es <c>null</c>, vacío o no es un objeto JSON con esta forma. Nunca lanza: un perfil
    /// viejo o roto se lee como que no hay perfil.</summary>
    public static ShopifyShopProfile? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return document.RootElement.Deserialize<ShopifyShopProfile>(Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private ShopifyShopProfile Normalized(int limit) => new(
        Cut(Email, limit),
        Cut(ContactEmail, limit),
        Cut(CurrencyCode, limit),
        Cut(IanaTimezone, limit),
        TaxesIncluded,
        BillingAddress is null
            ? null
            : new ShopifyShopAddress(
                Cut(BillingAddress.Company, limit),
                Cut(BillingAddress.Address1, limit),
                Cut(BillingAddress.Address2, limit),
                Cut(BillingAddress.City, limit),
                Cut(BillingAddress.Province, limit),
                Cut(BillingAddress.ProvinceCode, limit),
                Cut(BillingAddress.Zip, limit),
                Cut(BillingAddress.CountryCode, limit),
                Cut(BillingAddress.Phone, limit)));

    private static string? Cut(string? text, int limit)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || limit == 0)
        {
            return null;
        }

        return trimmed.Length <= limit ? trimmed : trimmed[..limit];
    }
}
