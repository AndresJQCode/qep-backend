namespace Modules.Siigo.Application.Invoicing;

/// <summary>
/// Spec 2026-11-18 §6.2: la parte facturada del pedido como el cliente que se crea en Siigo. Persona: el primer token del nombre son los
/// nombres y el resto los apellidos (un solo token se repite en los dos; V-6). Empresa: la razón social, un solo elemento. NIT: sin lo que
/// va después del guion y sólo con dígitos. Consumidor final: el documento genérico de la DIAN con tipo CC (V-3).
/// </summary>
public static class SiigoCustomerMapper
{
    /// <summary>La ciudad que lleva un consumidor final sin dirección (Bogotá): sólo se usa para crearlo si Siigo no lo tiene.</summary>
    private const string FinalConsumerCity = "11001";
    private const string FinalConsumerState = "11";

    /// <summary>El máximo de dígitos del indicativo y del número en la documentación de Siigo (crear cliente).</summary>
    private const int PhonePartMaxLength = 10;

    /// <summary><c>null</c> si no se puede armar: sin identificación, con un tipo sin código de Siigo o sin ciudad con código DANE. Quien
    /// llama ya reportó cada caso como faltante.</summary>
    public static SiigoCustomerRequest? Map(SiigoBilledPartyInput party)
    {
        ArgumentNullException.ThrowIfNull(party);
        if (party.IsFinalConsumer)
        {
            return new SiigoCustomerRequest(
                "Person",
                SiigoIdentificationTypes.CodeFor(SiigoIdentificationKind.Cc)!,
                SiigoIdentificationTypes.FinalConsumerIdentification,
                ["Consumidor", "Final"],
                Clean(party.Address),
                "Co",
                Clean(party.StateCode) ?? FinalConsumerState,
                Clean(party.CityCode) ?? FinalConsumerCity,
                Phone(party.Phone),
                "Consumidor",
                "Final",
                Clean(party.Email));
        }

        if (party.IdentificationType is not { } kind
            || SiigoIdentificationTypes.CodeFor(kind) is not { } code
            || Identification(party) is not { Length: > 0 } identification
            || Clean(party.CityCode) is not { } city
            || Clean(party.StateCode) is not { } state)
        {
            return null;
        }

        var isCompany = party.PersonType == SiigoPersonKind.Juridica;
        var (first, rest) = SplitName(party.Name);
        return new SiigoCustomerRequest(
            isCompany ? "Company" : "Person",
            code,
            identification,
            isCompany ? [party.Name.Trim()] : [first, rest],
            Clean(party.Address),
            "Co",
            state,
            city,
            Phone(party.Phone),
            first,
            rest,
            Clean(party.Email));
    }

    /// <summary>El documento tal como se busca y se crea en Siigo: el NIT sin dígito de verificación y sólo con números; los demás, sin
    /// espacios en los bordes. <c>null</c> si queda vacío.</summary>
    public static string? Identification(SiigoBilledPartyInput party)
    {
        ArgumentNullException.ThrowIfNull(party);
        if (party.IsFinalConsumer)
        {
            return SiigoIdentificationTypes.FinalConsumerIdentification;
        }

        var raw = party.Identification?.Trim() ?? string.Empty;
        if (party.IdentificationType == SiigoIdentificationKind.Nit)
        {
            var dash = raw.IndexOf('-', StringComparison.Ordinal);
            raw = dash >= 0 ? raw[..dash] : raw;
            raw = new string(raw.Where(char.IsAsciiDigit).ToArray());
        }

        return raw.Length == 0 ? null : raw;
    }

    private static (string First, string Remainder) SplitName(string name)
    {
        var tokens = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length switch
        {
            0 => (string.Empty, string.Empty),
            1 => (tokens[0], tokens[0]),
            _ => (tokens[0], string.Join(' ', tokens.Skip(1))),
        };
    }

    // El teléfono nunca tumba la creación: lo que Siigo no aceptaría se omite en vez de mandarse.
    private static SiigoPhone? Phone(SiigoPhone? phone)
    {
        if (phone is null || PhonePart(phone.Number) is not { } number)
        {
            return null;
        }

        return new SiigoPhone(PhonePart(phone.Indicative), number);
    }

    // Sólo los dígitos ASCII; null si no queda ninguno o si pasan del máximo.
    private static string? PhonePart(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        return digits.Length is 0 or > PhonePartMaxLength ? null : digits;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
