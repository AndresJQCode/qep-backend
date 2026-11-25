using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-11-14 §2 (<c>shopify.orders.applied_projection</c>): los valores que QEP escribió en el pedido a partir de la versión
/// de Shopify aplicada. Sirve para saber si el asesor tocó un grupo: si el valor actual del pedido difiere de la proyección, lo
/// cambió QEP y una edición de Shopify sobre ese grupo es conflicto (§3).
/// <para>Se arma del pedido <b>leído después de escribir</b> (<see cref="IShopifyOrderWriter.ReadStateAsync"/>), no del borrador:
/// el dominio normaliza al guardar (parte, notas, redondeo) y comparar con el borrador daría conflictos falsos (plan D4). Las
/// comparaciones son en forma QEP: líneas como multiconjunto de variante, cantidad, precio y descuento; parte de envío con la ciudad
/// resuelta y los textos recortados.</para>
/// <para><see cref="Attributes"/> son los atributos de Shopify aplicados (no viven en el pedido: el asesor los corrige en la fila del
/// espejo, <c>qep_attributes</c>), y <see cref="Document"/> el documento del comprador como lo leyó la tienda.</para>
/// </summary>
public sealed record ShopifyOrderProjection(
    IReadOnlyList<ShopifyOrderLineState> Lines,
    ShopifyShippingDraft Shipping,
    ShopifyShippingFingerprint? ShippingParty,
    string? BuyerNotes,
    ShopifyBuyerDocument? Document,
    IReadOnlyDictionary<string, string> Attributes)
{
    // Un número nacional tiene al menos 7 dígitos en la práctica (Colombia: 10, celular y fijo con 60x).
    private const int MinNationalDigits = 7;

    private const int MaxCountryCodeDigits = 3;

    // camelCase, como el payload del espejo (ShopifyOrderIngestion.WritePayload): el SQL lo lee igual. Las tildes van tal cual (como
    // los detalles del importador), no como í.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static ShopifyOrderProjection From(
        ShopifyOrderState order, ShopifyBuyerDocument? document, IReadOnlyDictionary<string, string> attributes)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(attributes);
        return new(order.Lines, order.Shipping, order.ShippingParty, order.BuyerNotes, document, attributes);
    }

    public static string Write(ShopifyOrderProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return JsonSerializer.Serialize(projection, Json);
    }

    public static ShopifyOrderProjection Read(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var projection = JsonSerializer.Deserialize<ShopifyOrderProjection>(json, Json)
            ?? throw new InvalidOperationException("The applied projection is empty.");
        // Una proyección sin atributos (null en el JSON) es lo mismo que sin ninguno.
        return projection.Attributes is null ? projection with { Attributes = new Dictionary<string, string>() } : projection;
    }

    /// <summary>Las mismas líneas como multiconjunto de variante, cantidad, precio y descuento. <c>2.0</c> y <c>2</c> son el
    /// mismo valor (los decimales se comparan por valor, no por escala). La tasa no cuenta: fuera de COP el pedido la guarda en 0.</summary>
    public static bool SameLines(IReadOnlyList<ShopifyOrderLineState> a, IReadOnlyList<ShopifyOrderLineState> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count != b.Count)
        {
            return false;
        }

        var remaining = b.Select(Key).ToList();
        foreach (var key in a.Select(Key))
        {
            var index = remaining.IndexOf(key);
            if (index < 0)
            {
                return false;
            }

            remaining.RemoveAt(index);
        }

        return true;
    }

    /// <summary>La misma parte de envío: textos recortados, el teléfono sólo por sus dígitos y sin el indicativo de país
    /// (<see cref="SamePhone"/>), la misma ciudad y departamento (fuera de Colombia, el mismo país, ciudad escrita y región) y
    /// el mismo código postal. El
    /// correo no cuenta: Shopify no lo trae en la dirección (sale del comprador) y no se corrige desde ahí.</summary>
    public static bool SameParty(ShopifyShippingFingerprint? a, ShopifyShippingFingerprint? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return string.Equals(Text(a.Name), Text(b.Name), StringComparison.Ordinal)
            && SamePhone(a.Phone, b.Phone)
            && string.Equals(Text(a.Address), Text(b.Address), StringComparison.Ordinal)
            && a.DepartmentId == b.DepartmentId
            && a.CityId == b.CityId
            && string.Equals(Text(a.Country), Text(b.Country), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Text(a.CityName), Text(b.CityName), StringComparison.Ordinal)
            && a.RegionId == b.RegionId
            && string.Equals(Text(a.PostalCode), Text(b.PostalCode), StringComparison.Ordinal);
    }

    /// <summary>
    /// Review I-3: el mismo teléfono con o sin indicativo de país. Shopify puede guardar en E.164 (<c>+573001112233</c>) lo que QEP
    /// mandó nacional (<c>3001112233</c>), y el eco de una corrección no puede salir como conflicto. Se comparan sólo los dígitos; si
    /// difieren, el más corto tiene que ser el final del más largo, con al menos <see cref="MinNationalDigits"/> dígitos, y lo que
    /// sobra adelante, de 1 a <see cref="MaxCountryCodeDigits"/> dígitos (un indicativo E.164 tiene de 1 a 3). El mínimo evita que un
    /// fragmento («2233») case con cualquier número; el tope, que un número corto case con el final de otro más largo.
    /// </summary>
    public static bool SamePhone(string? a, string? b)
    {
        var (x, y) = (Digits(a), Digits(b));
        if (string.Equals(x, y, StringComparison.Ordinal))
        {
            return true;
        }

        var (shorter, longer) = x.Length <= y.Length ? (x, y) : (y, x);
        return shorter.Length >= MinNationalDigits
            && longer.Length - shorter.Length <= MaxCountryCodeDigits
            && longer.EndsWith(shorter, StringComparison.Ordinal);
    }

    // decimal.Equals compara por valor, así que la tupla ya ignora la escala.
    private static (Guid VariantId, decimal Quantity, decimal UnitPrice, decimal DiscountPercentage) Key(ShopifyOrderLineState line) =>
        (line.VariantId, line.Quantity, line.UnitPrice, line.DiscountPercentage);

    private static string? Text(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
}
