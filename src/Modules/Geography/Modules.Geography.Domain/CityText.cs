using System.Globalization;
using System.Text;

namespace Modules.Geography.Domain;

/// <summary>
/// La forma en la que se comparan los nombres de ciudad: la usan el emparejamiento por nombre
/// (<c>NameMatching</c>), los sinónimos y las ciudades sin resolver. Una sola regla para que lo que
/// se guarda y lo que se busca nunca diverjan.
/// </summary>
public static class CityText
{
    /// <summary>Ancho de <c>normalized_text</c>, <c>display_text</c> y <c>city_text</c>.</summary>
    public const int MaxLength = 120;

    /// <summary>
    /// Recorta, quita las marcas diacríticas, pasa a mayúsculas invariantes y colapsa todo espacio
    /// interno (espacios, tabs, saltos de línea) en uno solo. Sólo agrega coincidencias respecto de la
    /// normalización anterior (D-C1).
    /// </summary>
    public static string Normalize(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }
}
