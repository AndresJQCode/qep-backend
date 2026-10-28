using System.Text;
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

    /// <summary>
    /// Igual que <see cref="Interpolate(string, RunVariables)"/>, pero cada valor pasa por <paramref name="encode"/> y el texto de la plantilla
    /// no. Fallo P2-R31: la ruta del bloque HTTP escapa lo que dijo el cliente (<c>Uri.EscapeDataString</c>) sin escapar la ruta que escribió
    /// el admin.
    /// </summary>
    public static string Interpolate(string template, RunVariables variables, Func<string, string> encode)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(encode);
        return Reference().Replace(template, match => encode(variables.TextOf(match.Groups[1].Value)));
    }

    /// <summary>
    /// Fallo P2-R11: el cuerpo JSON del bloque HTTP. Una variable dentro de una cadena JSON (<c>"{nota}"</c>) se reemplaza por su texto
    /// escapado como JSON, sin agregar comillas; una suelta (<c>{cliente.id}</c>), por su literal JSON: número, booleano, objeto o lista tal
    /// cual, texto entre comillas y escapado, y <c>null</c> si no existe. Así una comilla o una barra del cliente no rompen el cuerpo ni le
    /// agregan campos. Lo que devuelve no se valida acá: quien llama lo vuelve a leer como JSON antes de mandarlo.
    /// </summary>
    public static string InterpolateJson(string template, RunVariables variables)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(variables);
        var result = new StringBuilder(template.Length);
        var scan = new JsonStringScan();
        var last = 0;
        foreach (Match match in Reference().Matches(template))
        {
            scan.Advance(template, last, match.Index);
            result.Append(template, last, match.Index - last);
            var path = match.Groups[1].Value;
            result.Append(scan.InString ? RunVariables.JsonStringContent(variables.TextOf(path)) : RunVariables.JsonLiteral(variables.Get(path)));
            last = match.Index + match.Length;
        }

        result.Append(template, last, template.Length - last);
        return result.ToString();
    }

    /// <summary>Cada ruta referenciada, en orden de aparición y sin quitar repetidas. El validador compara la raíz de cada una.</summary>
    public static IReadOnlyList<string> ReferencesIn(string template) =>
        [.. Reference().Matches(template).Select(match => match.Groups[1].Value)];

    // Si el texto literal de la plantilla deja la posición dentro de una cadena JSON. El token reemplazado no se recorre: su texto no tiene
    // comillas ni barras, y lo que lo reemplaza ya va escapado.
    private struct JsonStringScan
    {
        private bool escaped;

        public bool InString { get; private set; }

        public void Advance(string text, int from, int to)
        {
            for (var index = from; index < to; index++)
            {
                var character = text[index];
                if (!InString)
                {
                    InString = character == '"';
                }
                else if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    InString = false;
                }
            }
        }
    }
}
