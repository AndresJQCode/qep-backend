using System.Globalization;
using System.Text;

namespace Modules.Quotations.Domain;

public static class CarrierName
{
    public const int MaxLength = 60;
    public const int ExternalMaxLength = 120;
    public const int MaxExternalNames = 20;

    /// <summary>
    /// Forma comparable de un nombre: sin mayúsculas, tildes ni espacios repetidos. El índice
    /// único de <c>quotations.carrier_names</c> depende de este algoritmo exacto; cambiarlo
    /// deja nombres ya guardados sin normalizar.
    /// </summary>
    public static string Normalize(string value)
    {
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var decomposed = collapsed.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var stripped = new string(decomposed
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray());
        return stripped.Normalize(NormalizationForm.FormC);
    }
}
