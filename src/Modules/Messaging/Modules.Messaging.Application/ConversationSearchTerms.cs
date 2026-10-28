namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-09 §8.7: cuándo la búsqueda de la lista también busca por número
/// (<c>wa_id LIKE '%dígitos%'</c>). Sólo si el término parece un número: un dígito suelto («Laura 2»,
/// «Calle 10») traería casi todas las conversaciones (revisión de la Task 14).</summary>
public static class ConversationSearchTerms
{
    /// <summary>Mínimo de dígitos para buscar por número.</summary>
    public const int MinimumDigits = 3;

    /// <summary>Los dígitos del término si, sin <c>+</c>, espacios ni guiones, tiene al menos
    /// <see cref="MinimumDigits"/> dígitos y éstos son al menos el 80 % de los caracteres; si no, <c>null</c>.</summary>
    public static string? NumberDigits(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        var compact = term.Where(character => character is not ('+' or '-') && !char.IsWhiteSpace(character)).ToArray();
        var digits = new string(compact.Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= MinimumDigits && digits.Length * 5 >= compact.Length * 4 ? digits : null;
    }
}
