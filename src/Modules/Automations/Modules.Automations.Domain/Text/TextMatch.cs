using System.Globalization;
using System.Text;

namespace Modules.Automations.Domain.Text;

/// <summary>
/// Spec 2026-10-21 D-C6 y §4.3: la palabra clave se compara «sin distinguir mayúsculas ni tildes». Esta es la única normalización:
/// el agregado la aplica al guardar las palabras y el motor (plan 2) la aplica al mensaje del cliente, así que los dos lados
/// siempre comparan lo mismo.
/// </summary>
public static class TextMatch
{
    /// <summary>Minúsculas invariantes y sin marcas diacríticas («Cotización» → «cotizacion»). No recorta espacios: quien guarda
    /// o compara decide si recorta.</summary>
    public static string Normalize(string value)
    {
        // FormD separa la letra de su tilde para poder descartar la marca; FormC deja el resultado en la forma compuesta de siempre.
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}
