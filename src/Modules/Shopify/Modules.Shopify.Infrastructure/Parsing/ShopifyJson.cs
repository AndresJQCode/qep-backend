using System.Globalization;
using System.Text.Json;
using Modules.Shopify.Application;

namespace Modules.Shopify.Infrastructure.Parsing;

/// <summary>
/// Lectura de <see cref="JsonElement"/> común a <see cref="RestOrderParser"/> y <see cref="GraphQlOrderParser"/>. Ausente y
/// <c>null</c> son lo mismo (Shopify omite o anula según la versión del API); un valor presente con el tipo equivocado es
/// <see cref="ShopifyPayloadException"/>, nunca un cero o un nulo en silencio.
/// </summary>
internal static class ShopifyJson
{
    private const string GidPrefix = "gid://shopify/";

    public static void EnsureObject(JsonElement element, string format)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ShopifyPayloadException(string.Empty, $"The Shopify {format} order must be a JSON object, not {element.ValueKind}.");
        }
    }

    /// <summary>La propiedad, o <c>null</c> si falta o vale <c>null</c>.</summary>
    public static JsonElement? Find(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    /// <summary>Recorre un camino de propiedades (<c>total_price_set.shop_money.amount</c>).</summary>
    public static JsonElement? FindPath(JsonElement element, string path)
    {
        JsonElement? current = element;
        foreach (var name in path.Split('.'))
        {
            current = current is { } value ? Find(value, name) : null;
        }

        return current;
    }

    public static string? OptionalString(JsonElement element, string name, string field)
    {
        if (Find(element, name) is not { } value)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(field, "a string", value);
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Requerido: presente y de tipo texto. Vacío sí pasa; su ancho lo juzga el dominio.</summary>
    public static string RequiredString(JsonElement element, string name, string field)
    {
        if (Find(element, name) is not { } value)
        {
            throw Missing(field);
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid(field, "a string", value);
    }

    public static bool Boolean(JsonElement element, string name, string field) =>
        Find(element, name) switch
        {
            null => false,
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            { } value => throw Invalid(field, "a boolean", value),
        };

    public static DateTimeOffset RequiredDate(JsonElement element, string name, string field) =>
        OptionalDate(element, name, field) ?? throw Missing(field);

    /// <summary>En UTC, para que el mismo instante dé el mismo valor desde REST (con el huso de la tienda) y GraphQL (en Z).</summary>
    public static DateTimeOffset? OptionalDate(JsonElement element, string name, string field)
    {
        if (Find(element, name) is not { } value)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToUniversalTime();
        }

        throw Invalid(field, "an ISO 8601 date", value);
    }

    /// <summary>Shopify manda los montos como texto (<c>"119000.00"</c>); también se acepta número.</summary>
    public static decimal Decimal(JsonElement value, string field)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw Invalid(field, "a decimal number", value);
    }

    /// <summary>El primer camino presente, en orden; <c>null</c> si ninguno.</summary>
    public static decimal? OptionalAmount(JsonElement element, string field, params string[] paths)
    {
        foreach (var path in paths)
        {
            if (FindPath(element, path) is { } value)
            {
                return Decimal(value, field);
            }
        }

        return null;
    }

    /// <summary>Un id numérico de REST o un GID de GraphQL (<c>gid://shopify/Order/123</c> → 123). Un GID de otro tipo es un
    /// error: un <c>Customer</c> donde se esperaba un <c>Order</c> no es el mismo número.</summary>
    public static long? OptionalId(JsonElement element, string name, string field, string gidType)
    {
        if (Find(element, name) is not { } value)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            var prefix = GidPrefix + gidType + "/";
            var digits = text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;
            var query = digits.IndexOf('?', StringComparison.Ordinal);
            if (query >= 0)
            {
                digits = digits[..query];
            }

            if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        throw Invalid(field, $"a numeric id or a gid://shopify/{gidType}/ id", value);
    }

    /// <summary>Un arreglo, o vacío si falta. Con <paramref name="connection"/>, también la conexión de GraphQL
    /// (<c>{ nodes: [...] }</c> o <c>{ edges: [{ node }] }</c>).</summary>
    public static IEnumerable<JsonElement> Items(JsonElement element, string name, string field, bool connection = false)
    {
        if (Find(element, name) is not { } value)
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Where(item => item.ValueKind != JsonValueKind.Null).ToArray();
        }

        if (connection && value.ValueKind == JsonValueKind.Object)
        {
            if (Find(value, "nodes") is { ValueKind: JsonValueKind.Array } nodes)
            {
                return nodes.EnumerateArray().Where(item => item.ValueKind != JsonValueKind.Null).ToArray();
            }

            if (Find(value, "edges") is { ValueKind: JsonValueKind.Array } edges)
            {
                return edges.EnumerateArray().Select(edge => Find(edge, "node")).OfType<JsonElement>().ToArray();
            }

            return [];
        }

        throw Invalid(field, "an array", value);
    }

    public static IReadOnlyList<string> Strings(JsonElement element, string name, string field) =>
        Items(element, name, field)
            .Select((item, index) => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw Invalid($"{field}[{index}]", "a string", item))
            .ToArray();

    /// <summary>Atributos del checkout (REST <c>note_attributes</c>, GraphQL <c>customAttributes</c>). Sin nombre no hay clave y se
    /// descarta; sin valor queda vacío; con la clave repetida gana el último, como en el checkout. El valor no se recorta.</summary>
    public static IReadOnlyDictionary<string, string> Attributes(JsonElement element, string name, string keyName, string field)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in Items(element, name, field))
        {
            var itemField = $"{field}[{index++}]";
            var key = OptionalString(item, keyName, $"{itemField}.{keyName}");
            if (key is not null)
            {
                attributes[key] = Find(item, "value") is { } value
                    ? value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()
                    : string.Empty;
            }
        }

        return attributes;
    }

    /// <summary>REST ya da los enums en minúsculas con guion bajo; GraphQL, en mayúsculas. Se lleva todo a la forma de REST.</summary>
    public static string? Enum(string? value) => value?.ToLowerInvariant();

    public static ShopifyPayloadException Missing(string field) =>
        new(field, $"The Shopify order payload has no '{field}'.");

    private static ShopifyPayloadException Invalid(string field, string expected, JsonElement value) =>
        new(field, $"The Shopify order payload field '{field}' must be {expected}, not {value.ValueKind}.");
}
