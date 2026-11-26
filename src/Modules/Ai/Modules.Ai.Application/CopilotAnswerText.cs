using System.Text.RegularExpressions;

namespace Modules.Ai.Application;

/// <summary>
/// Revisión de la rama, hallazgo 7 (spec 2026-11-17 copiloto D-C10 punto 4): los enlaces de una respuesta sólo salen de sus citas. El texto
/// que se guarda no lleva enlaces aunque el modelo los escriba (un dato guardado con instrucciones puede pedírselo): un enlace Markdown
/// —inline, de imagen o por referencia— deja su texto, la definición de una referencia se quita y una URL suelta o entre <c>&lt;&gt;</c> se
/// cambia por <see cref="Omitted"/>. Así no depende de cómo pinte el frontend. El resto del Markdown limitado (negritas, listas) queda igual.
/// </summary>
public static partial class CopilotAnswerText
{
    /// <summary>Lo que queda en lugar de una URL suelta.</summary>
    public const string Omitted = "[enlace omitido]";

    public static string Neutralize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var neutral = ReferenceDefinition().Replace(text, string.Empty);
        neutral = InlineLink().Replace(neutral, "$1");
        neutral = ReferenceLink().Replace(neutral, "$1");
        neutral = AutoLink().Replace(neutral, Omitted);
        neutral = BareUrl().Replace(neutral, Omitted);
        return ReferenceEquals(neutral, text) || neutral == text ? text : neutral.Trim();
    }

    // [1]: https://… en su propia línea.
    [GeneratedRegex(@"(?m)^[ \t]{0,3}\[[^\]\n]+\]:[^\n]*(\n|$)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ReferenceDefinition();

    // [texto](destino) y ![alt](destino); el destino admite un nivel de paréntesis (javascript:alert(1)).
    [GeneratedRegex(@"!?\[([^\]\n]*)\]\((?:[^()\n]|\([^()\n]*\))*\)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InlineLink();

    // [texto][ref].
    [GeneratedRegex(@"!?\[([^\]\n]+)\]\[[^\]\n]*\]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ReferenceLink();

    // <https://…>, <mailto:…>.
    [GeneratedRegex(@"<(?:[a-zA-Z][a-zA-Z0-9+.-]*:|www\.)[^>\s]+>", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AutoLink();

    // https://…, ftp://…, www.….
    [GeneratedRegex(@"\b(?:(?:https?|ftp)://|www\.)[^\s<>()\[\]]+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BareUrl();
}
