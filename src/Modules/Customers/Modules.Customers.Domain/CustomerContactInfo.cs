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

    /// <summary>
    /// El pais del cliente, ISO-3166-1 alpha-2 (<c>CO</c>, <c>ES</c>). Decide cual de los dos
    /// carriles de ciudad aplica — ver <see cref="CityId"/> y <see cref="CityName"/>.
    ///
    /// Se guarda el codigo y no el nombre: el nombre depende del idioma en el que se pinte la
    /// ficha, el codigo no. El catalogo de paises vive en el frontend
    /// (<c>features/customers/utils/countries.ts</c>); aca solo se exige que sean dos letras,
    /// porque no existe un modulo de geografia mundial contra el cual validarlo.
    /// </summary>
    public required string Country { get; init; }

    /// <summary>
    /// La ciudad DIVIPOLA del domicilio, FK blanda a <c>geography.cities</c>. **Solo para
    /// Colombia**: DIVIPOLA es el estandar colombiano y no describe ninguna ciudad de afuera.
    /// Nula en un cliente extranjero, que usa <see cref="CityName"/>.
    /// </summary>
    public Guid? CityId { get; init; }

    /// <summary>
    /// La ciudad escrita a mano, **solo para un cliente que no es de Colombia**. Es texto libre a
    /// proposito: no hay catalogo mundial de ciudades que este producto pueda mantener, y pedir
    /// uno para poder facturarle a un cliente de Madrid seria cambiar el problema por otro mas
    /// grande.
    /// </summary>
    public string? CityName { get; init; }

    // Espejan los anchos de columna. Salen del schema del formulario que ya existe
    // (customer-form.schema.ts); el del correo no esta ahi: 254 es el maximo de una direccion por
    // RFC 5321, el mismo que ya usa CompanyContactInfo.
    public const int PhoneMaxLength = 32;

    public const int EmailMaxLength = 254;

    public const int AddressMaxLength = 200;

    public const int CountryLength = 2;

    // El mismo ancho que el nombre de una ciudad DIVIPOLA en geography.cities, para que las dos
    // ciudades quepan en la misma celda de cualquier listado.
    public const int CityNameMaxLength = 120;

    // La coherencia entre el pais y los dos carriles de ciudad no se decide acá sino en
    // Customer.EnsureValidLocation, que es donde vive customers.customer.city_required desde antes
    // de la libreta; Update la comprueba antes de asignar nada para conservar el todo-o-nada. Acá
    // sólo se normaliza cada campo por separado.
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
        Country = NormalizeCountry(Country),
        CityId = CityId,
        CityName = NormalizeCityName(CityName)
    };

    // A mayusculas por la misma razon que el correo va a minusculas: "co" y "CO" son el mismo
    // pais, y dejar las dos formas en base rompe en silencio la comparacion con
    // Customer.ColombiaCountryCode — el cliente quedaria tratado como extranjero.
    private static string NormalizeCountry(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            throw new CustomersDomainException(
                "customers.customer.country_required",
                "The customer country is required.");
        }

        var trimmed = country.Trim();
        return trimmed.Length == CountryLength && trimmed.All(char.IsAsciiLetter)
            ? trimmed.ToUpperInvariant()
            : throw new CustomersDomainException(
                "customers.customer.country_invalid",
                $"The customer country must be an ISO-3166-1 alpha-2 code ({CountryLength} letters).");
    }

    // Vacio y ausente son lo mismo, igual que con la razon social: un formulario que manda la
    // ciudad en blanco no esta guardando una cadena vacia. Que haga falta o no lo decide
    // Customer.EnsureValidLocation segun el pais.
    private static string? NormalizeCityName(string? cityName)
    {
        if (string.IsNullOrWhiteSpace(cityName))
        {
            return null;
        }

        var trimmed = cityName.Trim();
        return trimmed.Length > CityNameMaxLength
            ? throw new CustomersDomainException(
                "customers.customer.city_name_too_long",
                $"The customer city cannot exceed {CityNameMaxLength} characters.")
            : trimmed;
    }

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
