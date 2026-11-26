using System.Text;
using System.Text.RegularExpressions;

namespace Modules.Support.Domain;

/// <summary>Una variable de los documentos legales como la ofrece la consola: el nombre que va entre llaves, la etiqueta del menú y
/// un ejemplo para la vista previa (spec 2026-11-16, V-1).</summary>
public sealed record SupportLegalVariable(string Name, string Label, string Example);

/// <summary>Los valores con que se resuelve un documento al servirlo. Los de la empresa son null cuando el formulario no tiene empresa
/// que responde (publicado antes de D-8): entonces sus tokens no se pueden resolver.</summary>
public sealed record SupportLegalVariableValues(
    string TenantName, string? OrganizationName, string? OrganizationDocumentTypeCode, string? OrganizationDocumentNumber);

/// <summary>
/// Las variables de los documentos legales del portal (spec 2026-11-16, D-F8, V-1 y V-2): <c>{{nombre}}</c>, con espacios opcionales
/// adentro. La única lista del módulo: la consola la recibe en <c>GET …/settings/legal-documents</c>, la publicación rechaza lo que no
/// está en ella y el portal resuelve sólo lo que está en ella. Lo guardado conserva el token; se resuelve al servir.
/// </summary>
public static partial class SupportLegalVariables
{
    public const string Company = "empresa";

    public const string CompanyDocumentType = "tipo_documento";

    public const string CompanyDocumentNumber = "documento_identificacion";

    public const string TradeName = "nombre_comercial";

    public static IReadOnlyList<SupportLegalVariable> All { get; } =
    [
        new(Company, "Empresa que responde", "Comercial Andina S.A.S."),
        new(CompanyDocumentType, "Tipo de documento de la empresa", "NIT"),
        new(CompanyDocumentNumber, "Número de documento de la empresa", "900123456-7"),
        new(TradeName, "Nombre del negocio", "Comercial Andina"),
    ];

    private static readonly HashSet<string> Names = All.Select(variable => variable.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Los contenidos de todo <c>{{…}}</c> que no son una variable de la lista, una vez cada uno y en orden de aparición. Se mira
    /// cualquier cosa entre llaves dobles, no sólo lo que parece un nombre: así <c>{{Empresa}}</c> o <c>{{}}</c> se rechazan al publicar
    /// en vez de llegar al portal como texto literal. Lo que esto acepta, <see cref="FillHtml"/> lo resuelve: los dos usan
    /// <see cref="NameOf"/>.
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
            if (NameOf(inner) is null && !unknown.Contains(inner.Trim(), StringComparer.Ordinal))
            {
                unknown.Add(inner.Trim());
            }
        }

        return unknown;
    }

    /// <summary>El HTML de un documento con sus variables resueltas y <b>codificadas como HTML</b>: una razón social con <c>&amp;</c> o
    /// <c>&lt;</c> no rompe ni inyecta marcado, y lo resuelto no hace falta volver a sanearlo. Null si un token de la lista no tiene
    /// valor.</summary>
    public static string? FillHtml(string html, SupportLegalVariableValues values) => Fill(html, values, encode: true);

    /// <summary>Un texto plano (la pregunta de autorización, V-2) con sus variables resueltas tal cual: el portal lo pinta como texto, y
    /// codificarlo mostraría <c>&amp;amp;</c> al visitante. Null si un token de la lista no tiene valor.</summary>
    public static string? FillText(string text, SupportLegalVariableValues values) => Fill(text, values, encode: false);

    private static string? Fill(string text, SupportLegalVariableValues values, bool encode)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(values);
        var unresolved = false;
        var filled = AnyToken().Replace(text, match =>
        {
            // Un token desconocido sólo llega acá desde una fila escrita a mano: la validación es al publicar, así que se deja como está.
            if (NameOf(match.Groups[1].Value) is not { } name)
            {
                return match.Value;
            }

            var value = ValueOf(name, values);
            if (value is null)
            {
                unresolved = true;
                return match.Value;
            }

            return encode ? Encode(value) : value;
        });

        return unresolved ? null : filled;
    }

    private static string? ValueOf(string name, SupportLegalVariableValues values) =>
        name switch
        {
            Company => values.OrganizationName,
            CompanyDocumentType => values.OrganizationDocumentTypeCode,
            CompanyDocumentNumber => values.OrganizationDocumentNumber,
            TradeName => values.TenantName,
            _ => null,
        };

    // El contenido de unas llaves dobles es una variable si, sin los espacios de los lados, es un nombre de la lista (minúsculas y guion
    // bajo, V-1).
    private static string? NameOf(string inner)
    {
        var trimmed = inner.Trim();
        return Names.Contains(trimmed) ? trimmed : null;
    }

    // Lo mínimo para texto y para atributos entre comillas. No uso WebUtility.HtmlEncode porque además escribe como entidad los
    // caracteres latinos (í → &#237;) y la foto legal quedaría ilegible para quien la lea en la base.
    private static string Encode(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => character.ToString(),
            });
        }

        return builder.ToString();
    }

    // Cualquier cosa entre llaves dobles que no tenga llaves adentro.
    [GeneratedRegex(@"\{\{([^{}]*)\}\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AnyToken();
}
