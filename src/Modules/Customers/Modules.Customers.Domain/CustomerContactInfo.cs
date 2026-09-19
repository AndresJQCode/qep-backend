namespace Modules.Customers.Domain;

/// <summary>
/// Los datos de contacto del cliente, agrupados: teléfono, correo y su domicilio (calle y
/// ciudad). Los cuatro son obligatorios al escribir.
///
/// Van juntos y no como parámetros sueltos de <c>Create</c>/<c>Update</c> por la misma razón por
/// la que existe <c>CompanyContactInfo</c>. Las propiedades son <c>init</c> y no posicionales, así
/// que sólo se construye por nombre.
///
/// El domicilio es **del cliente, no de su libreta** (spec 2026-09-18). <c>CLI-DIR-01</c> lo había
/// movido a la fila principal de <see cref="CustomerAddress"/>, y desde entonces marcar otra
/// dirección de envío como principal cambiaba dónde está el cliente, y el siguiente PUT de la
/// ficha pisaba esa dirección. La libreta quedó como catálogo de destinos de envío; este par
/// volvió a <see cref="Customer.Address"/> y <see cref="Customer.CityId"/>. La ciudad es un
/// <see cref="Guid"/> y no un id fuertemente tipado de Geography: FK blanda a otro módulo, mismo
/// criterio que <see cref="CustomerAddress.CityId"/>.
/// </summary>
public sealed record CustomerContactInfo
{
    public string? Phone { get; init; }

    public string? Email { get; init; }

    public required string Address { get; init; }

    public required Guid CityId { get; init; }

    // Espejan los anchos de columna. Salen del schema del formulario que ya existe
    // (customer-form.schema.ts); el del correo no esta ahi: 254 es el maximo de una direccion por
    // RFC 5321, el mismo que ya usa CompanyContactInfo.
    public const int PhoneMaxLength = 32;

    public const int EmailMaxLength = 254;

    public const int AddressMaxLength = 200;

    // La ciudad no se valida acá sino en Customer.EnsureValidCityId, que es donde vive
    // customers.customer.city_required desde antes de la libreta; Update la comprueba antes de
    // asignar nada para conservar el todo-o-nada.
    internal CustomerContactInfo Normalized() => new()
    {
        Phone = NormalizeRequired(
            Phone,
            PhoneMaxLength,
            "customers.customer.phone_required",
            "The customer phone is required.",
            "customers.customer.phone_too_long",
            $"The customer phone cannot exceed {PhoneMaxLength} characters."),
        Email = NormalizeEmail(Email),
        Address = NormalizeRequired(
            Address,
            AddressMaxLength,
            "customers.customer.address_required",
            "The customer address is required.",
            "customers.customer.address_too_long",
            $"The customer address cannot exceed {AddressMaxLength} characters."),
        CityId = CityId
    };

    // Telefono y correo son obligatorios al crear y al editar. Las propiedades siguen siendo
    // string? a proposito: las filas anteriores a la regla pueden tener null en base (las columnas
    // no pasaron a NOT NULL), y el agregado tiene que poder leerlas. La regla muerde al escribir,
    // no al materializar.
    private static string NormalizeRequired(
        string? value,
        int maxLength,
        string requiredCode,
        string requiredMessage,
        string tooLongCode,
        string tooLongMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CustomersDomainException(requiredCode, requiredMessage);
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new CustomersDomainException(tooLongCode, tooLongMessage)
            : trimmed;
    }

    // A minusculas por el mismo criterio que CompanyContactInfo: "Compras@Verde.CO" y
    // "compras@verde.co" son la misma casilla, y dejar las dos formas en base obliga a cada
    // consumidor a normalizar de nuevo.
    private static string NormalizeEmail(string? email)
    {
        var trimmed = NormalizeRequired(
            email,
            EmailMaxLength,
            "customers.customer.email_required",
            "The customer email is required.",
            "customers.customer.email_too_long",
            $"The customer email cannot exceed {EmailMaxLength} characters.");

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
