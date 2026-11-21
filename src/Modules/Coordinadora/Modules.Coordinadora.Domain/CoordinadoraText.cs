using System.Globalization;
using System.Text;

namespace Modules.Coordinadora.Domain;

/// <summary>Los recortes y la normalización que comparten los agregados y el armador.</summary>
public static class CoordinadoraText
{
    /// <summary>Recorta a <paramref name="maxLength"/> sin partir un par sustituto; <c>null</c> o vacío quedan <c>null</c>.</summary>
    public static string? Cap(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        var cut = maxLength;
        if (char.IsHighSurrogate(trimmed[cut - 1]))
        {
            cut--;
        }

        return trimmed[..cut];
    }

    /// <summary>
    /// Si un nombre de transportadora es Coordinadora (spec 2026-11-21 §5.3): contiene «coordinadora» sin distinguir mayúsculas ni
    /// tildes. Es la comparación con la foto <c>CarrierName</c> del pedido, no con el agregado <c>Carrier</c> de Quotations, que este
    /// módulo no ve.
    /// </summary>
    public static bool IsCoordinadora(string? carrierName) =>
        !string.IsNullOrWhiteSpace(carrierName)
        && Fold(carrierName).Contains("coordinadora", StringComparison.Ordinal);

    /// <summary>Minúsculas, sin tildes ni espacios sobrantes.</summary>
    public static string Fold(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
