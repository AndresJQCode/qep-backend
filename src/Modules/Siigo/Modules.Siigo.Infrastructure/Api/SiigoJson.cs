using System.Globalization;
using System.Text.Json;
using Modules.Siigo.Application;

namespace Modules.Siigo.Infrastructure.Api;

/// <summary>
/// Lectura tolerante de las respuestas de Siigo y escritura del cliente que se crea. Siigo responde una lista como arreglo en la raíz o
/// como <c>{ "pagination", "results": [...] }</c>, los ids como número o como texto y los nombres de propiedad no siempre con la misma
/// capitalización: se leen sin distinguir mayúsculas y un valor que falta no tumba la lectura. Nada de lo que se lee aquí se imprime.
/// </summary>
internal static class SiigoJson
{
    private const int MessageMaxLength = 1000;

    /// <summary>El documento, o <c>null</c> si el cuerpo no es JSON.</summary>
    public static JsonDocument? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Los elementos de una lista: la raíz si es un arreglo, o <c>results</c>. Sin cuerpo legible, ninguno. Los elementos viven
    /// mientras viva el <see cref="JsonDocument"/>, así que se copian con <c>Clone</c>.</summary>
    public static IReadOnlyList<JsonElement> Items(string body)
    {
        using var document = Parse(body);
        if (document is null)
        {
            return [];
        }

        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && Property(root, "results") is { ValueKind: JsonValueKind.Array } results)
        {
            root = results;
        }

        return root.ValueKind == JsonValueKind.Array
            ? [.. root.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(item => item.Clone())]
            : [];
    }

    public static (int? Page, int? PageSize, int? TotalResults) Pagination(string body)
    {
        using var document = Parse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object
            || Property(document.RootElement, "pagination") is not { ValueKind: JsonValueKind.Object } pagination)
        {
            return (null, null, null);
        }

        return (Int(pagination, "page"), Int(pagination, "page_size"), Int(pagination, "total_results"));
    }

    public static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty(name, out var exact))
        {
            return exact;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>Un valor escalar como texto: un número (<c>24446</c>) y un texto valen igual. Vacío o nulo es <c>null</c>.</summary>
    public static string? Text(JsonElement element, string name) =>
        Property(element, name) is { } value
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() is { Length: > 0 } text ? text.Trim() : null,
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    public static decimal? Decimal(JsonElement element, string name) =>
        Property(element, name) is { } value
            ? value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
                JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => number,
                _ => null,
            }
            : null;

    public static bool Bool(JsonElement element, string name, bool fallback) =>
        Property(element, name) is { } value && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static int? Int(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;

    /// <summary>El nombre que ve la persona: <c>name</c>; si no, el de la persona (<c>first_name last_name</c>); si no, el usuario.</summary>
    public static string NameOf(JsonElement item)
    {
        if (Text(item, "name") is { } name)
        {
            return name;
        }

        var person = string.Join(' ', new[] { Text(item, "first_name"), Text(item, "last_name") }.Where(part => part is not null));
        return person.Length > 0 ? person : Text(item, "username") ?? Text(item, "code") ?? string.Empty;
    }

    /// <summary>El CUFE de una factura ya enviada a la DIAN: <c>stamp.cufe</c> o <c>cufe</c>.</summary>
    public static string? Cufe(JsonElement invoice) =>
        (Property(invoice, "stamp") is { ValueKind: JsonValueKind.Object } stamp ? Text(stamp, "cufe") : null) ?? Text(invoice, "cufe");

    /// <summary>
    /// Los errores de una respuesta: <c>{ "Errors": [ { "Code", "Message" } ] }</c> (también un arreglo suelto, con <c>code</c> y
    /// <c>message</c> o textos). Sin ninguno legible, uno solo con el status, para que el rechazo nunca quede sin causa.
    /// </summary>
    public static IReadOnlyList<SiigoError> Errors(string body, System.Net.HttpStatusCode? status, bool fallbackToStatus = true)
    {
        using var document = Parse(body);
        var errors = new List<SiigoError>();
        if (document is not null)
        {
            var root = document.RootElement;
            var list = root.ValueKind == JsonValueKind.Array ? root : Property(root, "Errors") ?? Property(root, "errors");
            if (list is { ValueKind: JsonValueKind.Array } array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } message)
                    {
                        errors.Add(new SiigoError("error", Cap(message)));
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        errors.Add(new SiigoError(Text(item, "code") ?? "error", Cap(Text(item, "message") ?? string.Empty)));
                    }
                }
            }
        }

        return errors.Count > 0 || !fallbackToStatus ? errors : [new SiigoError($"http_{(int?)status ?? 0}", string.Empty)];
    }

    /// <summary>El cuerpo de <c>POST /v1/customers</c> (spec 2026-11-18 §6.2). No manda <c>fiscal_responsibilities</c> ni
    /// <c>vat_responsible</c> (V-6).</summary>
    public static string CustomerBody(SiigoCustomerRequest request)
    {
        var body = new Dictionary<string, object?>
        {
            ["type"] = "Customer",
            ["person_type"] = request.PersonType,
            ["id_type"] = request.IdTypeCode,
            ["identification"] = request.Identification,
            ["name"] = request.Name,
            ["active"] = true,
            ["address"] = new
            {
                address = request.Address,
                city = new { country_code = request.CountryCode, state_code = request.StateCode, city_code = request.CityCode },
            },
        };
        if (request.Phone is { } phone)
        {
            // Sin indicativo la propiedad no viaja: un diccionario la omite, un objeto anónimo la mandaría en null.
            var entry = new Dictionary<string, string>();
            if (phone.Indicative is { } indicative)
            {
                entry["indicative"] = indicative;
            }

            entry["number"] = phone.Number;
            body["phones"] = new[] { entry };
        }

        if (!string.IsNullOrWhiteSpace(request.ContactEmail) || !string.IsNullOrWhiteSpace(request.ContactFirstName))
        {
            body["contacts"] = new[]
            {
                new { first_name = request.ContactFirstName, last_name = request.ContactLastName, email = request.ContactEmail },
            };
        }

        return JsonSerializer.Serialize(body);
    }

    private static string Cap(string value) => value.Length <= MessageMaxLength ? value : value[..MessageMaxLength];
}
