using System.Globalization;
using System.Text;

namespace Modules.Automations.Domain.Text;

/// <summary>
/// Spec 2026-10-21 D-C6 y §4.3: la palabra clave se compara «sin distinguir mayúsculas ni tildes». Esta es la única normalización:
/// el agregado la aplica al guardar las palabras y el motor (plan 2) la aplica al mensaje del cliente, así que los dos lados
/// siempre comparan lo mismo. Ruling P2-R1: la normalización también recorta y colapsa los espacios, porque el cliente escribe
/// «  servicio   técnico » y debe coincidir con la palabra guardada.
/// </summary>
public static class TextMatch
{
    /// <summary>Minúsculas invariantes, sin marcas diacríticas, recortado y con cada racha de espacios reducida a uno solo
    /// («  Servicio   Técnico » → «servicio tecnico»).</summary>
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

        var lowered = builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        return string.Join(' ', lowered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Igualdad sin distinguir mayúsculas, tildes ni espacios de más; <c>null</c> cuenta como texto vacío.</summary>
    public static bool Same(string? a, string? b) => Normalize(a ?? string.Empty) == Normalize(b ?? string.Empty);

    /// <summary>Si el texto coincide (<see cref="Same"/>) con alguno de los candidatos.</summary>
    public static bool IsAnyOf(string? text, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var normalized = Normalize(text ?? string.Empty);
        return candidates.Any(candidate => Normalize(candidate) == normalized);
    }

    /// <summary>El operador <c>contains</c> del motor: el fragmento aparece dentro del texto, ya normalizados los dos. Un fragmento
    /// vacío no está en ningún texto (si no, una regla sin valor siempre se cumpliría).</summary>
    public static bool Contains(string? text, string? fragment)
    {
        var normalizedFragment = Normalize(fragment ?? string.Empty);
        return normalizedFragment.Length > 0 && Normalize(text ?? string.Empty).Contains(normalizedFragment, StringComparison.Ordinal);
    }
}
