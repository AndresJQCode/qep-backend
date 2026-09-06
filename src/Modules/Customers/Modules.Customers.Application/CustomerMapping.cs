using Modules.Customers.Domain;

namespace Modules.Customers.Application;

internal static class CustomerMapping
{
    /// <summary>
    /// Del agregado al DTO, con la ciudad, el departamento y la clasificacion ya resueltos por el
    /// llamador. No los resuelve esta funcion: cada handler decide como (una consulta puntual en
    /// Get/Create/Update/Activate/Deactivate, un lote en List) y esto solo ensambla.
    /// </summary>
    public static CustomerDto ToDto(
        this Customer customer,
        CustomerCityRef city,
        ClientClassification classification,
        IReadOnlyDictionary<Guid, CustomerCityRef> citiesById) => new(
        customer.Id.Value,
        customer.Cuc,
        customer.Name,
        customer.IdentificationType.ToWireValue(),
        customer.IdentificationNumber,
        customer.Phone,
        customer.Email,
        // `address`/`city`/`department` describen la **principal**: lo que la pantalla muestra
        // como "donde esta el cliente". La libreta entera viaja en `addresses`.
        customer.RequirePrincipalAddress().Address,
        new CustomerCityDto(city.CityId, city.CityDivipolaCode, city.CityName),
        new CustomerDepartmentDto(
            city.DepartmentId, city.DepartmentDivipolaCode, city.DepartmentName),
        classification.ToDto(),
        customer.Addresses
            .OrderByDescending(address => address.IsPrincipal)
            .ThenBy(address => address.Name)
            .Select(address => ToAddressDto(address, citiesById))
            .ToArray(),
        customer.WithRetention,
        customer.VatSurplus,
        customer.IsActive,
        customer.CreatedAt,
        customer.UpdatedAt);

    // La FK de base garantiza que la ciudad de cada direccion exista, asi que un miss aca es
    // corrupcion de datos: se prefiere un nombre vacio a tirar la ficha entera abajo, que es lo
    // que hace ToDtoAsync con la principal (esa si es estructural).
    private static CustomerAddressDto ToAddressDto(
        CustomerAddress address,
        IReadOnlyDictionary<Guid, CustomerCityRef> citiesById)
    {
        citiesById.TryGetValue(address.CityId, out var city);

        return new CustomerAddressDto(
            address.Id.Value,
            address.Name,
            address.Address,
            address.Phone,
            address.CityId,
            city?.CityName ?? string.Empty,
            city?.DepartmentId ?? Guid.Empty,
            city?.DepartmentName ?? string.Empty,
            address.IsPrincipal);
    }

    /// <summary>
    /// La version de un solo cliente: resuelve su ciudad y su clasificacion y arma el DTO. Para
    /// <c>GetCustomerHandler</c>, <c>DeactivateCustomerHandler</c> y <c>ActivateCustomerHandler</c>,
    /// que ya tienen el <c>Customer</c> en mano y no necesitan resolver nada mas antes.
    ///
    /// La FK de base garantiza que las dos referencias existan, asi que un miss aca es corrupcion
    /// de datos y no una entrada de usuario invalida — por eso lanza <see cref="InvalidOperationException"/>
    /// (500) y no un <see cref="CustomersDomainException"/> (422): no hay ningun campo del request
    /// que el llamador pueda corregir.
    /// </summary>
    public static async Task<CustomerDto> ToDtoAsync(
        this Customer customer,
        ICustomerGeographyLookup geographyLookup,
        IClientClassificationRepository classificationRepository,
        CancellationToken cancellationToken)
    {
        // Las ciudades de **todas** sus direcciones de una vez: la principal arma los campos
        // planos del DTO y el resto acompaña a cada dirección de la libreta.
        var citiesById = await geographyLookup.FindCitiesAsync(
            customer.Addresses.Select(address => address.CityId).Distinct().ToArray(),
            cancellationToken);
        var principalCityId = customer.RequirePrincipalAddress().CityId;
        var city = citiesById.TryGetValue(principalCityId, out var principalCity)
            ? principalCity
            : throw new InvalidOperationException(
                $"City '{principalCityId}' referenced by customer '{customer.Id}' " +
                "was not found.");
        var classification = await classificationRepository.FindAsync(
            customer.TenantId, customer.ClassificationId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Classification '{customer.ClassificationId}' referenced by customer " +
                $"'{customer.Id}' was not found.");

        return customer.ToDto(city, classification, citiesById);
    }

    /// <summary>
    /// Del contrato HTTP al dominio. <c>IdentificationTypeParser.Parse</c> es el que rechaza un
    /// tipo de documento desconocido; no se traduce a un valor por defecto, porque elegir NIT en
    /// silencio le cambia el documento al cliente sin que nadie se entere.
    /// </summary>
    public static CustomerIdentification ToIdentification(
        string identificationType,
        string identificationNumber) =>
        new()
        {
            Type = IdentificationTypeParser.Parse(identificationType),
            Number = identificationNumber
        };

    public static CustomerCommercialInfo ToCommercialInfo(
        Guid classificationId,
        bool withRetention,
        bool vatSurplus) =>
        new()
        {
            ClassificationId = new ClientClassificationId(classificationId),
            WithRetention = withRetention,
            VatSurplus = vatSurplus
        };
}
