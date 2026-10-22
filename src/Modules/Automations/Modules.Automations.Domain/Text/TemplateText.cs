using System.Text.RegularExpressions;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.Domain.Text;

/// <summary>
/// Interpolación de <c>{variable}</c> en los textos del flujo (spec 2026-10-21 D-M8). Una llave que no abre una ruta válida queda
/// literal: así el cuerpo JSON de una API (<c>{ "customerId": {cliente.id} }</c>) sobrevive a la interpolación.
/// </summary>
public static partial class TemplateText
{
    // Misma forma que <see cref="JsonPath"/>. El validador (FlowValidator.VariableToken) tiene su propia expresión, más laxa
    // (sin guion bajo inicial, acepta «a..b» o «a[x]») y sólo cuenta raíces: unificarlas cambiaría qué acepta al publicar.
    [GeneratedRegex(@"\{([a-zA-Z_][a-zA-Z0-9_]*(?:\.[a-zA-Z_][a-zA-Z0-9_]*|\[[0-9]+\])*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Reference();

    public static string Interpolate(string template, RunVariables variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        return Reference().Replace(template, match => variables.TextOf(match.Groups[1].Value));
    }

    /// <summary>Cada ruta referenciada, en orden de aparición y sin quitar repetidas. El validador compara la raíz de cada una.</summary>
    public static IReadOnlyList<string> ReferencesIn(string template) =>
        [.. Reference().Matches(template).Select(match => match.Groups[1].Value)];
}
