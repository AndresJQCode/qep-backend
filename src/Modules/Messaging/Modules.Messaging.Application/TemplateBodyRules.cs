using System.Text.RegularExpressions;

namespace Modules.Messaging.Application;

/// <summary>La regla del cuerpo de una plantilla: forma, longitud y, si hay lista cerrada, que cada variable esté en ella. La comparten la
/// creación libre (sin lista) y las plantillas del sistema (con la lista de su tipo).</summary>
internal static partial class TemplateBodyRules
{
    /// <summary>El mensaje de error del cuerpo, o <c>null</c> si es válido. Con <paramref name="allowedKeys"/> nulo basta con que las
    /// variables estén bien formadas.</summary>
    public static string? Check(string? body, IReadOnlySet<string>? allowedKeys = null)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 1024)
        {
            return TemplateMessages.Body;
        }

        if (LooseBrace().Matches(body).Any(match => !StrictVariable().IsMatch(match.Value)))
        {
            return TemplateMessages.BodyVariable;
        }

        if (string.IsNullOrWhiteSpace(StrictVariable().Replace(body, string.Empty)))
        {
            return TemplateMessages.Body;
        }

        if (allowedKeys is not null
            && StrictVariable().Matches(body).Any(match => !allowedKeys.Contains(match.Value[2..^2])))
        {
            return TemplateMessages.BodyVariableNotAllowed;
        }

        return null;
    }

    // Cualquier par de llaves dobles, con lo que sea adentro: lo que no cumpla la forma estricta se marca.
    [GeneratedRegex(@"\{\{[^{}]*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex LooseBrace();

    [GeneratedRegex(@"\{\{[a-z0-9_]+\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex StrictVariable();
}
