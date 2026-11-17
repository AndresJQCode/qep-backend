using System.Globalization;

namespace Modules.Customers.Domain;

/// <summary>
/// El formato con el que la importación por Excel bautiza una etiqueta que no existía (spec 2026-11-17): tipo oración, primera
/// letra en mayúscula y el resto en minúsculas, recortado. Sólo la importación lo usa; la pantalla de etiquetas guarda lo que
/// escribe el usuario, y una etiqueta que ya existe conserva su nombre aunque el Excel la traiga con otras mayúsculas.
/// </summary>
public static class TagNameFormat
{
    public static string SentenceCase(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        // Invariante a propósito: el tenant es colombiano pero la regla no debe depender de la cultura del hilo del worker.
        var lower = trimmed.ToLower(CultureInfo.InvariantCulture);
        var first = StringInfo.GetNextTextElement(lower);
        return string.Concat(first.ToUpper(CultureInfo.InvariantCulture), lower.AsSpan(first.Length));
    }
}
