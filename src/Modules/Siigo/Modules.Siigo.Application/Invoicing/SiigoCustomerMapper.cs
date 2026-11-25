namespace Modules.Siigo.Application.Invoicing;

/// <summary>
/// Spec 2026-11-18 §6.2: la parte facturada del pedido como el cliente que se crea en Siigo. Persona: el primer token del nombre son los
/// nombres y el resto los apellidos (un solo token se repite en los dos; V-6). Empresa: la razón social, un solo elemento. Documento: sin
/// separadores y el NIT sin dígito de verificación (<see cref="Identification"/>). Consumidor final: el documento genérico de la DIAN con
/// tipo CC (V-3).
/// </summary>
public static class SiigoCustomerMapper
{
    /// <summary>La ciudad que lleva un consumidor final sin dirección (Bogotá): sólo se usa para crearlo si Siigo no lo tiene.</summary>
    private const string FinalConsumerCity = "11001";
    private const string FinalConsumerState = "11";

    /// <summary>El máximo de dígitos del indicativo y del número en la documentación de Siigo (crear cliente).</summary>
    private const int PhonePartMaxLength = 10;

    /// <summary>Los pesos del dígito de verificación de la DIAN, desde la cifra de la derecha del número.</summary>
    private static readonly int[] CheckDigitWeights = [3, 7, 13, 17, 19, 23, 29, 37, 41, 43, 47, 53, 59, 67, 71];

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

    /// <summary>
    /// El documento tal como se busca, se crea y se factura en Siigo; lo guardado en QEP no cambia (spec 2026-11-25). CC: sólo dígitos.
    /// NIT: sólo dígitos y sin el de verificación (<see cref="NitWithoutCheckDigit"/>). CE y pasaporte: sólo letras y dígitos ASCII, con
    /// las mayúsculas como se escribieron. Otro tipo o sin tipo: sin espacios en los bordes. Consumidor final: el documento genérico.
    /// <c>null</c> si queda vacío.
    /// </summary>
    public static string? Identification(SiigoBilledPartyInput party)
    {
        ArgumentNullException.ThrowIfNull(party);
        if (party.IsFinalConsumer)
        {
            return SiigoIdentificationTypes.FinalConsumerIdentification;
        }

        var raw = party.Identification?.Trim() ?? string.Empty;
        var sent = party.IdentificationType switch
        {
            SiigoIdentificationKind.Cc => Digits(raw),
            SiigoIdentificationKind.Nit => NitWithoutCheckDigit(raw),
            SiigoIdentificationKind.Ce or SiigoIdentificationKind.Pasaporte =>
                new string(raw.Where(char.IsAsciiLetterOrDigit).ToArray()),
            _ => raw,
        };

        return sent.Length == 0 ? null : sent;
    }

    // Un guion seguido de un solo dígito al final es el de verificación. Sin él, sólo se quita el décimo dígito de un número que empieza
    // por 8 o 9 (el rango de los NIT de empresa) y cuyo último dígito cuadra con los nueve primeros; una cédula usada como NIT no se toca.
    private static string NitWithoutCheckDigit(string raw)
    {
        var dash = raw.LastIndexOf('-');
        if (dash >= 0 && raw[(dash + 1)..].TrimStart() is [var suffix] && char.IsAsciiDigit(suffix))
        {
            return Digits(raw[..dash]);
        }

        var digits = Digits(raw);
        return digits is ['8' or '9', _, _, _, _, _, _, _, _, var last] && last - '0' == CheckDigit(digits[..^1])
            ? digits[..^1]
            : digits;
    }

    // El algoritmo de la DIAN: la suma ponderada módulo 11; si el residuo pasa de 1, el dígito es 11 menos el residuo.
    private static int CheckDigit(string number)
    {
        var sum = 0;
        for (var position = 0; position < number.Length; position++)
        {
            sum += (number[^(position + 1)] - '0') * CheckDigitWeights[position];
        }

        var remainder = sum % 11;
        return remainder > 1 ? 11 - remainder : remainder;
    }

    private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());

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
        var digits = Digits(value ?? string.Empty);
        return digits.Length is 0 or > PhonePartMaxLength ? null : digits;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
