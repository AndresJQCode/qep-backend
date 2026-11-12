namespace Modules.Customers.Domain;

/// <summary>
/// Una direccion del cliente: a donde se le entrega. Entidad hija de <see cref="Customer"/> —sin
/// repositorio propio ni construccion fuera del agregado, mismo criterio que <c>PriceScale</c>
/// dentro de <c>Product</c>—, porque su unicidad ("una sola principal") es un invariante del
/// cliente, no de cada fila.
///
/// Reemplaza al par <c>address</c>/<c>city_id</c> que vivia en <c>customers</c>: un cliente tiene
/// una bodega, un local y la casa de quien recibe, y ese par solo alcanzaba para uno.
///
/// <para><b>La ciudad se guarda; el departamento no.</b> Es el de la ciudad, y guardarlo seria
/// una segunda verdad que puede contradecir a la primera. El formulario si lo muestra, porque el
/// combobox de ciudad filtra por el.</para>
/// </summary>
public sealed class CustomerAddress
{
    /// <summary>A quien pertenece la direccion ("Bodega Norte", "Casa de Ana"), no la calle. Es
    /// lo que la persona lee para elegir a donde entregar.</summary>
    public const int NameMaxLength = 120;

    public const int AddressMaxLength = 200;

    public const int PhoneMaxLength = 32;

    // EF Core materializa por aca. El codigo nunca construye la entidad asi: solo el agregado.
    private CustomerAddress()
    {
        Name = string.Empty;
        Address = string.Empty;
    }

    private CustomerAddress(
        CustomerAddressId id,
        CustomerId customerId,
        CustomerAddressDetails details,
        DateTimeOffset occurredAt)
    {
        Id = id;
        CustomerId = customerId;
        Name = string.Empty;
        Address = string.Empty;
        CreatedAt = occurredAt;
        Apply(details, occurredAt);
    }

    public CustomerAddressId Id { get; private set; }

    public CustomerId CustomerId { get; private set; }

    public string Name { get; private set; }

    public string Address { get; private set; }

    public string? Phone { get; private set; }

    /// <summary>El pais de la direccion, ISO-3166-1 alpha-2 en mayusculas. Decide cual de los dos
    /// carriles de ciudad vale: <c>CO</c> usa <see cref="CityId"/>, el resto <see cref="CityName"/>.
    /// Las filas anteriores a este campo son todas colombianas (default <c>CO</c> en la migracion).
    /// </summary>
    public string Country { get; private set; } = Customer.ColombiaCountryCode;

    /// <summary>FK blanda al modulo <c>Geography</c>, con el mismo criterio que tenia
    /// <c>Customer.CityId</c>: <see cref="Guid"/> y no un id fuertemente tipado de otro dominio.
    /// <c>null</c> en una direccion de afuera de Colombia: DIVIPOLA no describe sus ciudades.
    /// </summary>
    public Guid? CityId { get; private set; }

    /// <summary>La ciudad escrita a mano de una direccion de afuera. <c>null</c> en Colombia, que
    /// usa <see cref="CityId"/>; la base lo respalda con <c>CK_customer_addresses_city_lane</c>.
    /// </summary>
    public string? CityName { get; private set; }

    /// <summary>La que la cotizacion propone por defecto. El agregado garantiza que haya
    /// exactamente una mientras el cliente tenga direcciones; la base lo respalda con un indice
    /// unico parcial.</summary>
    public bool IsPrincipal { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    internal static CustomerAddress Create(
        CustomerId customerId,
        CustomerAddressDetails details,
        DateTimeOffset occurredAt) =>
        new(CustomerAddressId.New(), customerId, details, occurredAt);

    internal void Apply(CustomerAddressDetails details, DateTimeOffset occurredAt)
    {
        var normalized = details.Normalized();

        Name = normalized.Name;
        Address = normalized.Address;
        Phone = normalized.Phone;
        Country = normalized.Country;
        CityId = normalized.CityId;
        CityName = normalized.CityName;
        UpdatedAt = occurredAt;
    }

    // Sólo cambia la ciudad: el resto de la dirección es de quien la escribió (Customer.CorrectCity).
    internal void MoveToCity(Guid cityId, DateTimeOffset occurredAt)
    {
        CityId = cityId;
        UpdatedAt = occurredAt;
    }

    // Solo el agregado decide cual es la principal: hacerlo publico permitiria dejar dos, o
    // ninguna, sin que nadie lo note hasta que una cotizacion no sepa a donde entregar.
    internal void MarkPrincipal(bool isPrincipal, DateTimeOffset occurredAt)
    {
        if (IsPrincipal == isPrincipal)
        {
            return;
        }

        IsPrincipal = isPrincipal;
        UpdatedAt = occurredAt;
    }
}

/// <summary>Los datos de una direccion tal como entran al agregado. Value object: valida y
/// normaliza, misma relacion con <see cref="CustomerAddress"/> que <c>CustomerContactInfo</c> con
/// <c>Customer</c>.</summary>
public sealed record CustomerAddressDetails
{
    public required string Name { get; init; }

    public required string Address { get; init; }

    /// <summary>Sin pais, la direccion es colombiana: es lo que todos los llamadores anteriores
    /// al campo mandaban sin decirlo.</summary>
    public string Country { get; init; } = Customer.ColombiaCountryCode;

    /// <summary>Solo en Colombia (<c>CO</c>); en cualquier otro pais debe ser <c>null</c>.</summary>
    public Guid? CityId { get; init; }

    /// <summary>Solo fuera de Colombia; en <c>CO</c> debe ser <c>null</c>.</summary>
    public string? CityName { get; init; }

    public string? Phone { get; init; }

    internal CustomerAddressDetails Normalized()
    {
        var country = NormalizeCountry(Country);
        var cityName = NormalizeCityName(CityName);
        var cityId = EnsureCityLane(country, CityId, cityName);

        return new()
        {
        Name = NormalizeRequired(
            Name,
            NameMaxLengthOf(),
            "customers.address.name_required",
            "The address name is required.",
            "customers.address.name_too_long",
            $"The address name cannot exceed {CustomerAddress.NameMaxLength} characters."),
        Address = NormalizeRequired(
            Address,
            CustomerAddress.AddressMaxLength,
            "customers.address.address_required",
            "The address is required.",
            "customers.address.address_too_long",
            $"The address cannot exceed {CustomerAddress.AddressMaxLength} characters."),
            Country = country,
            CityId = cityId,
            CityName = cityName,
            Phone = NormalizeOptional(Phone)
        };
    }

    // A mayusculas por la misma razon que en CustomerContactInfo: "co" y "CO" son el mismo pais, y
    // dejar las dos formas en base rompe en silencio la comparacion con ColombiaCountryCode.
    private static string NormalizeCountry(string? country)
    {
        var trimmed = country?.Trim();
        return trimmed is { Length: CustomerContactInfo.CountryLength } && trimmed.All(char.IsAsciiLetter)
            ? trimmed.ToUpperInvariant()
            : throw new CustomersDomainException(
                "customers.address.country_invalid",
                $"The address country must be an ISO-3166-1 alpha-2 code ({CustomerContactInfo.CountryLength} letters).");
    }

    // Vacio y ausente son lo mismo, como con la ciudad del cliente.
    private static string? NormalizeCityName(string? cityName)
    {
        if (string.IsNullOrWhiteSpace(cityName))
        {
            return null;
        }

        var trimmed = cityName.Trim();
        return trimmed.Length > CustomerContactInfo.CityNameMaxLength
            ? throw new CustomersDomainException(
                "customers.address.city_name_too_long",
                $"The address city cannot exceed {CustomerContactInfo.CityNameMaxLength} characters.")
            : trimmed;
    }

    // Los dos carriles de ciudad (spec tienda publica 4.2): Colombia lleva el id DIVIPOLA y nada
    // de texto; el resto del mundo lleva el texto y ningun id. Mezclarlos no se corrige solo: se
    // rechaza, porque cual de los dos quiso decir quien escribio es una adivinanza.
    private static Guid? EnsureCityLane(string country, Guid? cityId, string? cityName)
    {
        if (country == Customer.ColombiaCountryCode)
        {
            if (cityId is null || cityId == Guid.Empty)
            {
                throw new CustomersDomainException(
                    "customers.address.city_required", "The address city is required.");
            }

            return cityName is null
                ? cityId
                : throw LaneInvalid("A Colombian address uses a city id, not a city name.");
        }

        // El id sobrante se rechaza antes que el texto faltante: es el error que dice cual de los
        // dos carriles se equivoco, y el texto se pide recien cuando el id ya no estorba.
        if (cityId is not null)
        {
            throw LaneInvalid("An address outside Colombia uses a city name, not a city id.");
        }

        return cityName is null
            ? throw new CustomersDomainException(
                "customers.address.city_required", "The address city is required.")
            : null;
    }

    private static CustomersDomainException LaneInvalid(string message) =>
        new("customers.address.city_lane_invalid", message);

    private static int NameMaxLengthOf() => CustomerAddress.NameMaxLength;

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

    // Vacio y ausente son lo mismo para un campo opcional, mismo criterio que
    // CustomerContactInfo.NormalizeOptional.
    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > CustomerAddress.PhoneMaxLength
            ? throw new CustomersDomainException(
                "customers.address.phone_too_long",
                $"The address phone cannot exceed {CustomerAddress.PhoneMaxLength} characters.")
            : trimmed;
    }
}
