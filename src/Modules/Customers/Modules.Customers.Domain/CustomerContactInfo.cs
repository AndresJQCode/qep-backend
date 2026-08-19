namespace Modules.Customers.Domain;

/// <summary>
/// Los datos de contacto y ubicacion del cliente, todos opcionales, agrupados.
///
/// Van juntos y no como cinco parametros sueltos de <c>Create</c>/<c>Update</c> por la misma razon
/// por la que existe <c>CompanyContactInfo</c>: los cinco son <c>string?</c>, y sueltos en la
/// firma nada impide intercambiarlos. Una ciudad en el departamento compila sin una queja.
/// Las propiedades son <c>init</c> y no posicionales, asi que solo se construye por nombre.
/// </summary>
public sealed record CustomerContactInfo
{
    public string? Phone { get; init; }

    public string? Email { get; init; }

    public string? Address { get; init; }

    /// <summary>Departamento. En el frontend viaja como <c>department</c>.</summary>
    public string? Department { get; init; }

    public string? City { get; init; }

    // Espejan los anchos de columna. Los cuatro primeros salen del schema del formulario que ya
    // existe (customer-form.schema.ts); el del correo no esta ahi: 254 es el maximo de una
    // direccion por RFC 5321, el mismo que ya usa CompanyContactInfo.
    public const int PhoneMaxLength = 32;

    public const int EmailMaxLength = 254;

    public const int AddressMaxLength = 200;

    public const int DepartmentMaxLength = 120;

    public const int CityMaxLength = 120;

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
            $"The customer address cannot exceed {AddressMaxLength} characters."),
        Department = NormalizeOptional(
            Department,
            DepartmentMaxLength,
            "customers.customer.department_too_long",
            $"The customer department cannot exceed {DepartmentMaxLength} characters."),
        City = NormalizeOptional(
            City,
            CityMaxLength,
            "customers.customer.city_too_long",
            $"The customer city cannot exceed {CityMaxLength} characters.")
    };

    // Vacio y ausente son lo mismo para un campo opcional. El formulario manda "" cuando el
    // usuario borra el input, y guardar esa cadena dejaria dos representaciones de "no hay dato"
    // que cada consumidor tendria que comparar.
    //
    // El departamento y la ciudad **no** se validan contra DANE/DIVIPOLA: `CLI-01` lo deja
    // explicitamente fuera de alcance y esa capability no existe. Hoy son texto libre.
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
