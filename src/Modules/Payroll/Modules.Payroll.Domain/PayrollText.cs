using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Modules.Payroll.Domain;

/// <summary>
/// Normalización de texto de Payroll: un solo lugar para que la unicidad de los nombres de concepto y la importación comparen igual
/// («Logística» y «LOGISTICA» son el mismo nombre). Es la misma forma que <c>HrText.Fold</c>: recorta, quita tildes, pasa a
/// minúsculas y colapsa espacios.
/// </summary>
public static partial class PayrollText
{
    public static string? Fold(string? text) => string.IsNullOrWhiteSpace(text) ? null : Normalize(text);

    private static string Normalize(string text)
    {
        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        var folded = builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        return Whitespace().Replace(folded, " ");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
