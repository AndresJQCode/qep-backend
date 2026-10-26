namespace Modules.Quotations.Application;

/// <summary>El teléfono tal como lo necesita WhatsApp: sólo dígitos, con indicativo.</summary>
public static class WhatsAppPhone
{
    // Mismo criterio que `buildWhatsAppLink` en el frontend (`whatsapp-link.ts`, ahora
    // retirado): `Customer.Phone` es texto libre sin indicativo obligatorio — un número de 10
    // dígitos se asume local (Colombia) y se le antepone 57; uno más largo se asume que ya lo
    // trae. Sin dígitos no hay a quién escribirle: `null`.
    public static string? Normalize(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return null;

        return digits.Length == 10 ? $"57{digits}" : digits;
    }
}
