using BuildingBlocks.Domain.Taxes;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el repositorio de <c>Customers</c> al puerto que <c>quotations</c> declara.
///
/// Vive acá y no en ninguno de los dos módulos, mismo criterio que <c>ProductImageLookup</c>
/// entre Catalog y Storage (CAT-05): ningún módulo de negocio referencia al otro, y el
/// composition root —que ya referencia a los dos— es el único lugar donde ese acoplamiento es
/// legítimo.
///
/// No decide nada: la regla de negocio (US-1/US-18, CUC presente y cliente activo) es de
/// <c>QuotationCustomerEligibility</c>, en Application.
/// </summary>
internal sealed class QuotationCustomerLookup(
    ICustomerRepository repository,
    ICustomerGeographyLookup geographyLookup,
    ICustomerTaxRateLookup taxRateLookup,
    ICustomerCompanyDirectory companies)
    : IQuotationCustomerLookup
{
    /// <summary>El tope de contactos que viajan en el documento (spec 2026-11-10 §4.6): el diálogo de envío no pagina.</summary>
    public const int ContactCap = 100;

    public async Task<QuotationClientContact?> FindContactAsync(
        Guid tenantId, Guid clientId, Guid contactId, CancellationToken cancellationToken)
    {
        var contact = await companies.FindContactOfAsync(tenantId, clientId, contactId, cancellationToken);
        return contact is null ? null : new QuotationClientContact(contact.Id, contact.Name, contact.Phone);
    }

    public async Task<IReadOnlyList<QuotationClientContact>> ListContactsAsync(
        Guid tenantId, Guid clientId, CancellationToken cancellationToken)
    {
        var contacts = await companies.ListContactsAsync(tenantId, clientId, ContactCap, cancellationToken);
        return [.. contacts.Select(contact => new QuotationClientContact(contact.Id, contact.Name, contact.Phone))];
    }

    public async Task<QuotationCustomerRef?> FindAsync(
        Guid tenantId, Guid clientId, CancellationToken cancellationToken)
    {
        var customer = await repository.FindAsync(
            tenantId, new CustomerId(clientId), cancellationToken);
        if (customer is null)
        {
            return null;
        }

        // Las ciudades del domicilio, de toda la libreta y de los perfiles de facturación de una vez: la cotizacion muestra
        // las dos libretas completas, y cada fila necesita el nombre de su ciudad. Regiones y tasas, igual: un lote.
        var citiesById = await geographyLookup.FindCitiesAsync(
            AddressCityIds(customer)
                .Concat(BillingProfileCityIds(customer))
                .Concat(DomicileCityIds(customer))
                .Distinct()
                .ToArray(),
            cancellationToken);
        var regionsById = await FindRegionsAsync([customer], cancellationToken);
        var ratesById = await FindTaxRatesAsync(tenantId, [customer], cancellationToken);

        return ToRef(customer, citiesById, regionsById, ratesById);
    }

    public async Task<IReadOnlyDictionary<Guid, QuotationCustomerRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> clientIds, CancellationToken cancellationToken)
    {
        var customers = await repository.FindManyAsync(
            tenantId,
            clientIds.Select(id => new CustomerId(id)).ToArray(),
            cancellationToken);
        if (customers.Count == 0)
        {
            return new Dictionary<Guid, QuotationCustomerRef>();
        }

        // Las ciudades del domicilio y de la libreta de todos los clientes del lote, de una
        // sola vez — mismo motivo que FindAsync, pero por lote y no por cliente.
        var citiesById = await geographyLookup.FindCitiesAsync(
            customers.Values
                .SelectMany(customer => AddressCityIds(customer)
                    .Concat(DomicileCityIds(customer)))
                .Distinct()
                .ToArray(),
            cancellationToken);
        var regionsById = await FindRegionsAsync(customers.Values, cancellationToken);
        var ratesById = await FindTaxRatesAsync(tenantId, customers.Values, cancellationToken);

        // El lote (el Excel de pedidos) no carga los perfiles de facturación: salen vacíos.
        return customers.Values.ToDictionary(
            customer => customer.Id.Value,
            customer => ToRef(customer, citiesById, regionsById, ratesById));
    }

    // Las tasas de los clientes y de sus perfiles de facturación, con una sola consulta para todo el lote; sin ninguna
    // asignada no se toca el catálogo.
    private async Task<IReadOnlyDictionary<Guid, CustomerTaxRateRef>> FindTaxRatesAsync(
        Guid tenantId, IEnumerable<Customer> customers, CancellationToken cancellationToken)
    {
        Guid[] rateIds =
        [
            .. customers
                .SelectMany(customer => customer.WithholdingTaxRateIds.Concat(
                    customer.BillingProfiles.SelectMany(profile => profile.WithholdingTaxRateIds)))
                .Distinct()
        ];
        return rateIds.Length == 0
            ? new Dictionary<Guid, CustomerTaxRateRef>()
            : await taxRateLookup.FindManyAsync(tenantId, rateIds, cancellationToken);
    }

    // WithRetention ya no es una columna del cliente: se deriva de sus tasas asignadas (spec impuestos por tipo §9,
    // client.withRetention sigue en la respuesta para el frontend viejo). Es «tiene alguna Retefuente», de cualquier
    // país. Una tasa borrada del catálogo no cuenta, y las de los perfiles de facturación tampoco.
    private static bool HoldsRetefuente(Customer customer, IReadOnlyDictionary<Guid, CustomerTaxRateRef> ratesById) =>
        customer.WithholdingTaxRateIds.Any(id =>
            ratesById.TryGetValue(id, out var rate) && rate.Type == TaxCountries.RetefuenteType);

    // The regions of the domiciles, of every address book and of the billing profiles in one call (provincia guardada):
    // none are asked for when no customer of the batch has one, which is the normal case for a Colombian tenant.
    private async Task<IReadOnlyDictionary<Guid, CustomerRegionRef>> FindRegionsAsync(
        IEnumerable<Customer> customers, CancellationToken cancellationToken)
    {
        Guid[] regionIds =
        [
            .. customers
                .SelectMany(customer => customer.Addresses
                    .Select(address => address.RegionId)
                    .Concat(customer.BillingProfiles.Select(profile => profile.RegionId))
                    .Append(customer.RegionId))
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
        ];
        return regionIds.Length == 0
            ? new Dictionary<Guid, CustomerRegionRef>()
            : await geographyLookup.FindRegionsAsync(regionIds, cancellationToken);
    }

    private static QuotationRegionDto? RegionOf(
        Guid? regionId, IReadOnlyDictionary<Guid, CustomerRegionRef> regionsById) =>
        regionId is { } id && regionsById.TryGetValue(id, out var region)
            ? new QuotationRegionDto(region.Id, region.Name)
            : null;

    // Las ciudades DIVIPOLA de la libreta: una direccion de afuera no tiene id que resolver.
    private static IEnumerable<Guid> AddressCityIds(Customer customer) =>
        customer.Addresses
            .Where(address => address.CityId.HasValue)
            .Select(address => address.CityId!.Value);

    private static IEnumerable<Guid> BillingProfileCityIds(Customer customer) =>
        customer.BillingProfiles
            .Where(profile => profile.CityId.HasValue)
            .Select(profile => profile.CityId!.Value);

    // La ciudad del domicilio a resolver, o nada: un cliente que no es de Colombia no tiene
    // ciudad DIVIPOLA, la suya es el texto de Customer.CityName.
    private static IEnumerable<Guid> DomicileCityIds(Customer customer) =>
        customer.CityId is { } cityId ? [cityId] : [];

    private static QuotationCustomerRef ToRef(
        Customer customer,
        IReadOnlyDictionary<Guid, CustomerCityRef> citiesById,
        IReadOnlyDictionary<Guid, CustomerRegionRef> regionsById,
        IReadOnlyDictionary<Guid, CustomerTaxRateRef> ratesById)
    {
        CustomerCityRef? contactCity = null;
        if (customer.CityId is { } domicileCityId)
        {
            citiesById.TryGetValue(domicileCityId, out contactCity);
        }

        return new QuotationCustomerRef(
            customer.Id.Value,
            customer.TenantId,
            customer.Cuc,
            customer.IsActive,
            customer.Name,
            customer.Phone,
            // El domicilio del cliente (spec 2026-09-18), no la principal de la libreta: es el
            // respaldo de facturacion y envio que QuotationResponseComposer.cs:96-110 entrega a
            // la cotizacion y al pedido, y lo que viaja en el WhatsApp. La principal sigue yendo
            // primera en Addresses, que es lo que el selector de envio preselecciona.
            customer.Address,
            HoldsRetefuente(customer, ratesById),
            customer.VatSurplus,
            customer.Email,
            contactCity?.CityId,
            // La ciudad del cliente para la cotizacion y el PDF: la DIVIPOLA si es colombiano, y
            // si no la que tiene escrita. Sin este `??` el cliente de afuera aparece sin ciudad en
            // la cotizacion aunque su ficha la muestre — QuotationCustomerRef.CityName es texto y
            // no distingue de donde salio.
            contactCity?.CityName ?? customer.CityName,
            contactCity?.DepartmentId,
            contactCity?.DepartmentName,
            customer.Addresses
                .OrderByDescending(address => address.IsPrincipal)
                .ThenBy(address => address.Name)
                .Select(address => ToAddressRef(address, citiesById, regionsById))
                .ToArray(),
            customer.UpdatedAt,
            customer.BusinessName,
            customer.IdentificationNumber,
            customer.IsComplete,
            customer.PersonType?.ToWireValue(),
            customer.Country,
            customer.IdentificationType?.ToWireValue(),
            RegionOf(customer.RegionId, regionsById),
            customer.BillingProfiles
                .OrderByDescending(profile => profile.IsPrincipal)
                .ThenBy(profile => profile.Name)
                .Select(profile => ToBillingProfileRef(profile, citiesById, regionsById, ratesById))
                .ToArray(),
            CustomerBillingProfile.MaxPerCustomer);
    }

    // La misma forma que la ficha del cliente (CustomerMapping.ToBillingProfileDto): ciudad y departamento resueltos en CO,
    // la ciudad escrita afuera, y las tasas que el catálogo todavía devuelve, ordenadas por país, tipo y nombre.
    private static QuotationCustomerBillingProfileRef ToBillingProfileRef(
        CustomerBillingProfile profile,
        IReadOnlyDictionary<Guid, CustomerCityRef> citiesById,
        IReadOnlyDictionary<Guid, CustomerRegionRef> regionsById,
        IReadOnlyDictionary<Guid, CustomerTaxRateRef> ratesById)
    {
        var city = profile.CityId is { } cityId ? citiesById.GetValueOrDefault(cityId) : null;
        return new QuotationCustomerBillingProfileRef(
            profile.Id.Value,
            profile.Name,
            profile.PersonType,
            profile.IdentificationType,
            profile.IdentificationNumber,
            profile.Phone,
            profile.Email,
            profile.Address,
            profile.Country,
            profile.CityId,
            city?.CityName ?? profile.CityName,
            city?.DepartmentId,
            city?.DepartmentName,
            RegionOf(profile.RegionId, regionsById),
            [.. profile.WithholdingTaxRateIds
                .Where(ratesById.ContainsKey)
                .Select(id => ratesById[id])
                .OrderBy(rate => rate.Country, StringComparer.Ordinal)
                .ThenBy(rate => rate.Type, StringComparer.Ordinal)
                .ThenBy(rate => rate.Name, StringComparer.Ordinal)
                .Select(rate => new QuotationClientWithholdingRateResponse(
                    rate.Id, rate.Name, rate.Type, rate.Country, rate.Percentage, rate.IsActive, rate.AppliesTo.ToString()))],
            profile.VatSurplus,
            profile.IsPrincipal);
    }

    private static QuotationCustomerAddressRef ToAddressRef(
        CustomerAddress address,
        IReadOnlyDictionary<Guid, CustomerCityRef> citiesById,
        IReadOnlyDictionary<Guid, CustomerRegionRef> regionsById)
    {
        // Una direccion de afuera no tiene ciudad DIVIPOLA: viaja sin ids y la ciudad escrita va como nombre.
        CustomerCityRef? city = null;
        if (address.CityId is { } cityId)
        {
            citiesById.TryGetValue(cityId, out city);
        }

        // Para Colombia el contrato no cambia: una ciudad que no se resolvió sigue llegando vacía.
        var foreign = !string.Equals(address.Country, "CO", StringComparison.OrdinalIgnoreCase);
        return new QuotationCustomerAddressRef(
            address.Id.Value,
            address.Name,
            address.Address,
            address.Phone,
            address.CityId,
            city?.CityName ?? address.CityName ?? (foreign ? null : string.Empty),
            city?.DepartmentId ?? (foreign ? null : Guid.Empty),
            city?.DepartmentName ?? (foreign ? null : string.Empty),
            address.IsPrincipal,
            address.Country,
            RegionOf(address.RegionId, regionsById));
    }

    public Task<IReadOnlySet<Guid>> SearchIdsByIdentificationAsync(
        Guid tenantId, string term, CancellationToken cancellationToken) =>
        repository.SearchIdsByIdentificationNumberAsync(tenantId, term, cancellationToken);

    public Task<IReadOnlySet<Guid>> SearchIdsByCucAsync(
        Guid tenantId, string term, CancellationToken cancellationToken) =>
        repository.SearchIdsByCucAsync(tenantId, term, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> clientIds, CancellationToken cancellationToken)
    {
        var names = await repository.FindNamesByIdsAsync(
            tenantId,
            clientIds.Select(id => new CustomerId(id)).ToArray(),
            cancellationToken);

        return names.ToDictionary(entry => entry.Key.Value, entry => entry.Value);
    }
}
