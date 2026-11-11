using Modules.Customers.Domain;

namespace Modules.Customers.Application;

internal static class CustomerMapping
{
    /// <summary>
    /// La primera fila de la libreta que siembran el alta y el completar una ficha, con el mismo par que el domicilio (spec
    /// 2026-09-18, decisión 3). <c>null</c> si no hay qué sembrar, y el cliente nace sin libreta: uno de afuera no tiene
    /// ciudad para la FK a <c>geography.cities</c>, y sin calle la fila no existe (<c>customers.address.address_required</c>
    /// sigue vigente para la libreta). Una fila inventada sería peor que no tenerla.
    /// </summary>
    public static CustomerAddressDetails? ToFirstAddress(
        string name, string? address, CustomerCityRef? city, string? phone) =>
        city is null || string.IsNullOrWhiteSpace(address)
            ? null
            : new CustomerAddressDetails
            {
                Name = name,
                Address = address,
                CityId = city.CityId,
                Phone = phone
            };

    /// <summary>
    /// Del agregado al DTO, con la ciudad, el departamento y la clasificacion ya resueltos por el
    /// llamador. No los resuelve esta funcion: cada handler decide como (una consulta puntual en
    /// Get/Create/Update/Activate/Deactivate, un lote en List) y esto solo ensambla.
    /// </summary>
    public static CustomerDto ToDto(
        this Customer customer,
        CustomerCityRef? city,
        ClientClassification? classification,
        IReadOnlyDictionary<Guid, CustomerCityRef> citiesById,
        IReadOnlyCollection<TagDto> tags,
        IReadOnlyDictionary<Guid, CustomerTaxRateRef> taxRatesById)
    {
        var withholdingTaxRates = ToWithholdingDtos(customer, taxRatesById);
        return new CustomerDto(
            customer.Id.Value,
            customer.Cuc,
            customer.Name,
            customer.BusinessName,
            customer.IdentificationType?.ToWireValue(),
            customer.IdentificationNumber,
            customer.Phone,
            customer.Email,
            // `address`/`city`/`department` describen el **domicilio del cliente** (spec 2026-09-18):
            // donde esta, lo que la ficha muestra arriba. Las direcciones de envio van en `addresses`,
            // y marcar otra como principal no mueve estos tres campos.
            customer.Address,
            customer.Country,
            // `city`/`department` viajan resueltos **solo** para un cliente colombiano. Uno de afuera
            // no tiene fila en geography.cities: su ciudad es `cityName`, texto libre. Quien pinte la
            // ficha usa `city?.Name ?? cityName`.
            city is null
                ? null
                : new CustomerCityDto(city.CityId, city.CityDivipolaCode, city.CityName),
            city is null
                ? null
                : new CustomerDepartmentDto(
                    city.DepartmentId, city.DepartmentDivipolaCode, city.DepartmentName),
            customer.CityName,
            // Spec 2026-10-10 §5.2: un incompleto no tiene clasificación y viaja en null.
            classification?.ToDto(),
            customer.Addresses
                .OrderByDescending(address => address.IsPrincipal)
                .ThenBy(address => address.Name)
                .Select(address => ToAddressDto(address, citiesById))
                .ToArray(),
            // Derivado (spec impuestos por tipo §9.2): retiene quien tiene una Retefuente asignada. Una sola ReteICA no alcanza,
            // igual que en el cálculo de la cotización.
            withholdingTaxRates.Any(rate => rate.Type == RetefuenteType),
            customer.VatSurplus,
            customer.IsActive,
            customer.CreatedAt,
            customer.UpdatedAt,
            customer.IsComplete,
            // Spec 2026-10-11 §4.2: la ficha y la modal se dibujan con un solo GET. El listado y el Excel no las
            // piden y pasan una colección vacía.
            tags,
            customer.ToConsentDto(),
            customer.InstagramUsername,
            PersonType: customer.PersonType?.ToWireValue(),
            WithholdingTaxRates: withholdingTaxRates);
    }

    private const string RetefuenteType = "Retefuente";

    // Del id guardado a lo que la ficha pinta. Un id que el catálogo ya no devuelve no puede pasar (la tasa «en uso» no se
    // desactiva ni se borra), pero si pasara, se prefiere omitirlo a tirar la ficha entera. Orden estable: país, tipo, nombre.
    private static CustomerWithholdingTaxRateDto[] ToWithholdingDtos(
        Customer customer, IReadOnlyDictionary<Guid, CustomerTaxRateRef> taxRatesById) =>
        [.. customer.WithholdingTaxRateIds
            .Where(taxRatesById.ContainsKey)
            .Select(id => taxRatesById[id])
            .OrderBy(rate => rate.Country, StringComparer.Ordinal)
            .ThenBy(rate => rate.Type, StringComparer.Ordinal)
            .ThenBy(rate => rate.Name, StringComparer.Ordinal)
            .Select(rate => new CustomerWithholdingTaxRateDto(
                rate.Id, rate.Name, rate.Type, rate.Country, rate.Percentage, rate.IsActive))];

    /// <summary>
    /// Las tasas de todos los clientes dados, con una sola consulta al catálogo (el listado y el Excel no pueden ir de a una).
    /// </summary>
    public static Task<IReadOnlyDictionary<Guid, CustomerTaxRateRef>> FindTaxRatesAsync(
        ICustomerTaxRateLookup taxRateLookup,
        Guid tenantId,
        IEnumerable<Customer> customers,
        CancellationToken cancellationToken) =>
        taxRateLookup.FindManyAsync(
            tenantId,
            [.. customers.SelectMany(customer => customer.WithholdingTaxRateIds).Distinct()],
            cancellationToken);

    // La FK de base garantiza que la ciudad de cada direccion exista, asi que un miss aca es
    // corrupcion de datos: se prefiere un nombre vacio a tirar la ficha entera abajo, que es lo
    // que hace ToDtoAsync con la ciudad del domicilio (esa si es estructural).
    private static CustomerAddressDto ToAddressDto(
        CustomerAddress address,
        IReadOnlyDictionary<Guid, CustomerCityRef> citiesById)
    {
        // Una direccion de afuera no tiene fila en geography.cities: su ciudad es el texto que
        // escribieron y no hay departamento que resolver.
        if (address.CityId is not { } cityId)
        {
            return new CustomerAddressDto(
                address.Id.Value,
                address.Name,
                address.Address,
                address.Phone,
                null,
                address.CityName ?? string.Empty,
                null,
                null,
                address.IsPrincipal,
                address.Country);
        }

        citiesById.TryGetValue(cityId, out var city);

        return new CustomerAddressDto(
            address.Id.Value,
            address.Name,
            address.Address,
            address.Phone,
            cityId,
            city?.CityName ?? string.Empty,
            city?.DepartmentId ?? Guid.Empty,
            city?.DepartmentName ?? string.Empty,
            address.IsPrincipal,
            address.Country);
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
        ICustomerTagRepository tagRepository,
        ICustomerTaxRateLookup taxRateLookup,
        CancellationToken cancellationToken)
    {
        // Las ciudades del domicilio y de todas las direcciones de envio de una vez: el domicilio
        // arma los campos planos del DTO y el resto acompaña a cada fila de la libreta. Un cliente
        // de afuera no aporta ciudad de domicilio: la suya es texto libre.
        var citiesById = await geographyLookup.FindCitiesAsync(
            CustomerCityIds.OfAddresses(customer)
                .Concat(CustomerCityIds.OfDomicile(customer))
                .Distinct()
                .ToArray(),
            cancellationToken);
        var city = CustomerCityIds.ResolveDomicile(customer, citiesById);
        // Spec 2026-10-10 §6.2: un incompleto no tiene clasificación que buscar.
        var classification = customer.ClassificationId is { } classificationId
            ? await classificationRepository.FindAsync(customer.TenantId, classificationId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Classification '{classificationId}' referenced by customer " +
                    $"'{customer.Id}' was not found.")
            : null;

        var tags = (await tagRepository.ListForCustomerAsync(customer.TenantId, customer.Id, cancellationToken))
            .Select(item => item.ToDto())
            .ToArray();

        var taxRatesById = await FindTaxRatesAsync(taxRateLookup, customer.TenantId, [customer], cancellationToken);

        return customer.ToDto(city, classification, citiesById, tags, taxRatesById);
    }

    /// <summary>
    /// Del contrato HTTP al dominio. <c>IdentificationTypeParser.Parse</c> es el que rechaza un
    /// tipo de documento desconocido; no se traduce a un valor por defecto, porque elegir NIT en
    /// silencio le cambia el documento al cliente sin que nadie se entere.
    /// </summary>
    public static CustomerIdentification? ToIdentification(
        string? identificationType,
        string? identificationNumber) =>
        string.IsNullOrWhiteSpace(identificationType) && string.IsNullOrWhiteSpace(identificationNumber)
            ? null
            : new()
            {
                Type = IdentificationTypeParser.Parse(identificationType),
                Number = identificationNumber!
            };

    public static CustomerCommercialInfo ToCommercialInfo(
        Guid? classificationId,
        bool vatSurplus) =>
        new()
        {
            ClassificationId = classificationId is { } id ? new ClientClassificationId(id) : null,
            VatSurplus = vatSurplus
        };
}
