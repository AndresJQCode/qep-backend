namespace Modules.Quotations.Application;

/// <summary>El teléfono tal como lo necesita WhatsApp: sólo dígitos, con indicativo.</summary>
public static class WhatsAppPhone
{
    // Un «+» dice que el número ya trae su indicativo (el E.164 que guardan los formularios con
    // selector de país): sus dígitos van tal cual. Sin «+» se mantiene el criterio de
    // `buildWhatsAppLink` en el frontend (`whatsapp-link.ts`, ahora retirado), porque
    // `Customer.Phone` puede ser texto libre sin indicativo: un número de 10 dígitos se asume
    // local (Colombia) y se le antepone 57; con cualquier otro largo se asume que ya lo trae.
    // Sin dígitos no hay a quién escribirle: `null`.
    public static string? Normalize(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return null;

        if (phone.TrimStart().StartsWith('+')) return digits;

        return digits.Length == 10 ? $"57{digits}" : digits;
    }
}
