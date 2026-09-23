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
    ICustomerGeographyLookup geographyLookup)
    : IQuotationCustomerLookup
{
    public async Task<QuotationCustomerRef?> FindAsync(
        Guid tenantId, Guid clientId, CancellationToken cancellationToken)
    {
        var customer = await repository.FindAsync(
            tenantId, new CustomerId(clientId), cancellationToken);
        if (customer is null)
        {
            return null;
        }

        // Las ciudades del domicilio y de toda la libreta de una vez: la cotizacion muestra la
        // libreta completa en su selector de envio, y cada fila necesita el nombre de su ciudad.
        var citiesById = await geographyLookup.FindCitiesAsync(
            customer.Addresses
                .Select(address => address.CityId)
                .Concat(DomicileCityIds(customer))
                .Distinct()
                .ToArray(),
            cancellationToken);

        return ToRef(customer, citiesById);
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
                .SelectMany(customer => customer.Addresses
                    .Select(address => address.CityId)
                    .Concat(DomicileCityIds(customer)))
                .Distinct()
                .ToArray(),
            cancellationToken);

        return customers.Values.ToDictionary(
            customer => customer.Id.Value, customer => ToRef(customer, citiesById));
    }

    // La ciudad del domicilio a resolver, o nada: un cliente que no es de Colombia no tiene
    // ciudad DIVIPOLA, la suya es el texto de Customer.CityName.
    private static IEnumerable<Guid> DomicileCityIds(Customer customer) =>
        customer.CityId is { } cityId ? [cityId] : [];

    private static QuotationCustomerRef ToRef(
        Customer customer, IReadOnlyDictionary<Guid, CustomerCityRef> citiesById)
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
            customer.WithRetention,
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
                .Select(address => ToAddressRef(address, citiesById))
                .ToArray(),
            customer.UpdatedAt,
            customer.BusinessName);
    }

    private static QuotationCustomerAddressRef ToAddressRef(
        CustomerAddress address,
        IReadOnlyDictionary<Guid, CustomerCityRef> citiesById)
    {
        citiesById.TryGetValue(address.CityId, out var city);

        return new QuotationCustomerAddressRef(
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
