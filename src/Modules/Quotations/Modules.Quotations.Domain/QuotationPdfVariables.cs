using System.Text.RegularExpressions;

namespace Modules.Quotations.Domain;

/// <summary>Una variable del contenido del PDF como la ofrece la consola: el nombre que va entre llaves, la etiqueta del menú y un
/// ejemplo para la vista previa (spec 2026-11-16 §4.3).</summary>
public sealed record QuotationPdfVariable(string Name, string Label, string Example);

/// <summary>Los valores con que se resuelve el contenido al generar el PDF, ya formateados como los imprime el documento. Los de la
/// empresa y la vigencia son null cuando el documento no los tiene: entonces salen vacíos, sin bloquear el PDF.</summary>
public sealed record QuotationPdfVariableValues(
    string? Company,
    string? CompanyTaxId,
    string Customer,
    string Number,
    string IssuedOn,
    string? ValidUntil,
    string Advisor,
    string Total);

/// <summary>
/// Las variables del contenido configurable del PDF (spec 2026-11-16 §4.3): <c>{{nombre}}</c>, con espacios opcionales adentro. La
/// única lista del módulo: la consola la recibe en <c>GET …/settings/pdf-content</c>, el guardado rechaza lo que no está en ella y el
/// PDF resuelve sólo lo que está en ella. Lo guardado conserva el token; se resuelve al generar. La sintaxis es la de
/// <c>SupportLegalVariables</c>, copiada y no referenciada: Quotations no ve a Support.
/// </summary>
public static partial class QuotationPdfVariables
{
    public const string Company = "empresa";

    public const string CompanyTaxId = "nit_empresa";

    public const string Customer = "cliente";

    public const string Number = "numero";

    public const string IssuedOn = "fecha";

    public const string ValidUntil = "vigencia";

    public const string Advisor = "asesor";

    public const string Total = "total";

    public static IReadOnlyList<QuotationPdfVariable> All { get; } =
    [
        new(Company, "Empresa que factura", "Comercial Andina S.A.S."),
        new(CompanyTaxId, "NIT de la empresa", "900123456-7"),
        new(Customer, "Cliente", "María Pérez"),
        new(Number, "Número del documento", "1001"),
        new(IssuedOn, "Fecha de emisión", "16 de noviembre de 2026"),
        new(ValidUntil, "Vigente hasta", "30 de noviembre de 2026"),
        new(Advisor, "Asesor", "Laura Gómez"),
        new(Total, "Total", "$ 1.250.000"),
    ];

    private static readonly HashSet<string> Names = All.Select(variable => variable.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Los contenidos de todo <c>{{…}}</c> que no son una variable de la lista, una vez cada uno y en orden de aparición. Se mira
    /// cualquier cosa entre llaves dobles, no sólo lo que parece un nombre: así <c>{{Empresa}}</c>, <c>{{}}</c> o un token partido por
    /// una etiqueta se rechazan al guardar en vez de llegar al PDF como texto literal. Lo que esto acepta, <see cref="Fill"/> lo
    /// resuelve: los dos usan <see cref="NameOf"/>.
    /// </summary>
    public static IReadOnlyList<string> UnknownIn(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var unknown = new List<string>();
        foreach (Match match in AnyToken().Matches(text))
        {
            var inner = match.Groups[1].Value;
            var trimmed = TrimSpaces(inner);
            if (NameOf(inner) is null && !unknown.Contains(trimmed, StringComparer.Ordinal))
            {
                unknown.Add(trimmed);
            }
        }

        return unknown;
    }

    /// <summary>El texto con cada token de la lista reemplazado por su valor tal cual (un valor null sale vacío). Un token desconocido
    /// sólo llega acá desde una fila escrita a mano, porque la validación es al guardar: se deja literal.</summary>
    public static string Fill(string text, QuotationPdfVariableValues values)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(values);
        return AnyToken().Replace(text, match =>
            NameOf(match.Groups[1].Value) is { } name ? ValueOf(name, values) ?? string.Empty : match.Value);
    }

    private static string? ValueOf(string name, QuotationPdfVariableValues values) =>
        name switch
        {
            Company => values.Company,
            CompanyTaxId => values.CompanyTaxId,
            Customer => values.Customer,
            Number => values.Number,
            IssuedOn => values.IssuedOn,
            ValidUntil => values.ValidUntil,
            Advisor => values.Advisor,
            Total => values.Total,
            _ => null,
        };

    // El contenido de unas llaves dobles es una variable si, sin los espacios de los lados, es un nombre de la lista.
    private static string? NameOf(string inner)
    {
        var trimmed = TrimSpaces(inner);
        return Names.Contains(trimmed) ? trimmed : null;
    }

    // Sólo el espacio común: un espacio duro (que el editor mete como &nbsp; o como el carácter) no lo ve quien escribe, y aceptarlo
    // haría que el token se resolviera o no según cómo lo guardó el navegador.
    private static string TrimSpaces(string inner) => inner.Trim(' ');

    // Cualquier cosa entre llaves dobles que no tenga llaves adentro.
    [GeneratedRegex(@"\{\{([^{}]*)\}\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AnyToken();
}
