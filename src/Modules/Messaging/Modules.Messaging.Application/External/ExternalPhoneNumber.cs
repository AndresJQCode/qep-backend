namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-30 §6.3.2: el número que manda el integrador, en la forma que WhatsApp llama <c>wa_id</c> (dígitos, sin «+»).
/// No adivina país: sin indicativo el número no es válido.</summary>
public static class ExternalPhoneNumber
{
    private const int MinLength = 8;
    private const int MaxLength = 15;

    /// <summary>Quita «+», espacios y «-». Exige solo dígitos ASCII, de 8 a 15, sin cero inicial (ni prefijo 00). En falla, <paramref name="waId"/> queda vacío.</summary>
    public static bool TryNormalize(string? raw, out string waId)
    {
        waId = string.Empty;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        var digits = raw.Replace("+", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        if (digits.Length is < MinLength or > MaxLength || digits[0] == '0' || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        waId = digits;
        return true;
    }
}

/// <summary>Decisión del owner 2026-10-30: el número se valida de verdad (no basta con que tenga dígitos). Messaging no ve Customers: el
/// adaptador vive en Bootstrapper, sobre el normalizador de teléfonos.</summary>
public interface IDialablePhoneNumbers
{
    /// <summary>Si <paramref name="waId"/> (dígitos con indicativo, sin «+») es un número internacional real.</summary>
    bool IsDialable(string waId);
}
