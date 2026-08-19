namespace Modules.Companies.Domain;

/// <summary>
/// Los tres datos de contacto opcionales de una empresa, agrupados.
///
/// Van juntos y no como tres parametros sueltos de <c>Create</c>/<c>Update</c> por la misma razon
/// por la que <c>ProductDetails</c> existe en catalogo: los tres son <c>string?</c>, y sueltos en
/// la firma nada impide intercambiarlos. Un telefono en la direccion compila sin una queja.
/// Las propiedades son <c>init</c> y no posicionales, asi que solo se construye por nombre.
/// </summary>
public sealed record CompanyContactInfo
{
    public string? Phone { get; init; }

    public string? Email { get; init; }

    public string? Address { get; init; }

    // Espejan los anchos de columna, igual que Name y AccountNumber en Company: un valor
    // demasiado largo falla como 422 con codigo de dominio en vez de llegar a PostgreSQL y
    // volver como 500 server.unexpected.
    //
    // Los tres primeros salen del schema del formulario que ya existe en el frontend
    // (features/companies/types/company-form.schema.ts). El del correo no esta ahi: 254 es el
    // maximo de una direccion de correo por RFC 5321, y es el que se elige mientras nadie
    // documente otro.
    public const int PhoneMaxLength = 32;

    public const int EmailMaxLength = 254;

    public const int AddressMaxLength = 200;

    public static CompanyContactInfo Empty { get; } = new();

    /// <summary>
    /// Normaliza y hace cumplir los invariantes. Lo llama <see cref="Company"/>; no es punto de
    /// entrada publico, del mismo modo que <c>Company.Create</c> es el unico que construye el
    /// agregado.
    /// </summary>
    internal CompanyContactInfo Normalized() => new()
    {
        Phone = NormalizeOptional(
            Phone,
            PhoneMaxLength,
            "companies.company.phone_too_long",
            $"The company phone cannot exceed {PhoneMaxLength} characters."),
        Email = NormalizeEmail(Email),
        Address = NormalizeOptional(
            Address,
            AddressMaxLength,
            "companies.company.address_too_long",
            $"The company address cannot exceed {AddressMaxLength} characters.")
    };

    // Vacio y ausente son lo mismo para un campo opcional. El formulario manda "" cuando el
    // usuario borra el input (el schema de zod acepta z.literal('')), y guardar esa cadena
    // dejaria dos representaciones de "no hay dato" que cada consumidor tendria que comparar.
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
            ? throw new CompaniesDomainException(tooLongCode, tooLongMessage)
            : trimmed;
    }

    // A minusculas por la misma razon por la que ProductDetails pasa la moneda a mayusculas:
    // "Contacto@Andes.CO" y "contacto@andes.co" son la misma casilla, y dejar las dos formas en
    // base obliga a cada consumidor a normalizar de nuevo.
    private static string? NormalizeEmail(string? email)
    {
        var trimmed = NormalizeOptional(
            email,
            EmailMaxLength,
            "companies.company.email_too_long",
            $"The company email cannot exceed {EmailMaxLength} characters.");

        if (trimmed is null)
        {
            return null;
        }

        return IsPlausibleEmail(trimmed)
            ? trimmed.ToLowerInvariant()
            : throw new CompaniesDomainException(
                "companies.company.email_invalid",
                "The company email is not a valid address.");
    }

    // Comprobacion estructural, no una expresion regular: la sintaxis completa de RFC 5322 no se
    // valida con un patron sin abrir la puerta al backtracking catastrofico, y lo unico que hace
    // falta aca es rechazar lo que evidentemente no es una direccion. La validacion que el
    // formulario ve por campo la hace CompanyWriteRules con EmailAddress(); esta es la red del
    // dominio, que corre igual si alguien construye el agregado sin pasar por el validador.
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
