using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Modules.Hr.Domain;

/// <summary>
/// La forma comparable de un nombre: recorta, quita tildes, pasa a minúsculas y colapsa espacios. Con ella se decide la
/// unicidad de la configuración y, desde Application, <c>HrText.Fold</c> (la importación compara etiquetas igual): una sola
/// normalización para que «Logística» y «LOGISTICA» sean el mismo nombre en los dos lados.
/// </summary>
public static partial class OrganizationNames
{
    public static string Normalize(string name)
    {
        var decomposed = name.Trim().Normalize(NormalizationForm.FormD);
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
