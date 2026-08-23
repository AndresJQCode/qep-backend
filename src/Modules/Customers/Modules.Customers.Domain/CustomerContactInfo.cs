namespace Modules.Customers.Domain;

/// <summary>
/// Los datos de contacto del cliente, todos opcionales, agrupados.
///
/// Van juntos y no como tres parametros sueltos de <c>Create</c>/<c>Update</c> por la misma razon
/// por la que existe <c>CompanyContactInfo</c>. Las propiedades son <c>init</c> y no posicionales,
/// asi que solo se construye por nombre.
///
/// **Ya no lleva ciudad ni departamento.** Ese par vivio aca como texto libre hasta que la FK a
/// <c>Modules.Geography</c> los reemplazo: dejaron de ser "info de contacto libre" —ahora son una
/// relacion estructural del agregado, obligatoria— y se movieron a <see cref="Customer.CityId"/>,
/// de primer nivel. Ver ahi el porque no es un id fuertemente tipado de Geography.
/// </summary>
public sealed record CustomerContactInfo
{
    public string? Phone { get; init; }

    public string? Email { get; init; }

    public string? Address { get; init; }

    // Espejan los anchos de columna. Salen del schema del formulario que ya existe
    // (customer-form.schema.ts); el del correo no esta ahi: 254 es el maximo de una direccion por
    // RFC 5321, el mismo que ya usa CompanyContactInfo.
    public const int PhoneMaxLength = 32;

    public const int EmailMaxLength = 254;

    public const int AddressMaxLength = 200;

    public static CustomerContactInfo Empty { get; } = new();

    internal CustomerContactInfo Normalized() => new()
    {
        Phone = NormalizeOptional(
            Phone,
            PhoneMaxLength,
            "customers.customer.phone_too_long",
            $"The customer phone cannot exceed {PhoneMaxLength} characters."),
        Email = NormalizeEmail(Email),
        Address = NormalizeOptional(
            Address,
            AddressMaxLength,
            "customers.customer.address_too_long",
            $"The customer address cannot exceed {AddressMaxLength} characters.")
    };

    // Vacio y ausente son lo mismo para un campo opcional. El formulario manda "" cuando el
    // usuario borra el input, y guardar esa cadena dejaria dos representaciones de "no hay dato"
    // que cada consumidor tendria que comparar.
    private static string? NormalizeOptional(
        string? value,
        int maxLength,
        string tooLongCode,
        string tooLongMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new CustomersDomainException(tooLongCode, tooLongMessage)
            : trimmed;
    }

    // A minusculas por el mismo criterio que CompanyContactInfo: "Compras@Verde.CO" y
    // "compras@verde.co" son la misma casilla, y dejar las dos formas en base obliga a cada
    // consumidor a normalizar de nuevo.
    private static string? NormalizeEmail(string? email)
    {
        var trimmed = NormalizeOptional(
            email,
            EmailMaxLength,
            "customers.customer.email_too_long",
            $"The customer email cannot exceed {EmailMaxLength} characters.");

        if (trimmed is null)
        {
            return null;
        }

        return IsPlausibleEmail(trimmed)
            ? trimmed.ToLowerInvariant()
            : throw new CustomersDomainException(
                "customers.customer.email_invalid",
                "The customer email is not a valid address.");
    }

    // Comprobacion estructural, no una expresion regular: la sintaxis completa de RFC 5322 no se
    // valida con un patron sin abrir la puerta al backtracking catastrofico, y lo unico que hace
    // falta aca es rechazar lo que evidentemente no es una direccion. La validacion por campo la
    // hace CustomerWriteRules con EmailAddress(); esta es la red del dominio, que corre igual si
    // alguien construye el agregado sin pasar por el validador. Copiado a proposito de
    // CompanyContactInfo: son dos modulos, y compartirlo los acoplaria por una utilidad.
    private static bool IsPlausibleEmail(string value)
    {
        if (value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != value.LastIndexOf('@'))
        {
            return false;
        }

        var domain = value[(at + 1)..];
        var dot = domain.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && dot < domain.Length - 1;
    }
}
