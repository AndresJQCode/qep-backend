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

        // Las ciudades de todas sus direcciones de una vez: la cotizacion muestra la libreta
        // completa en su selector de envio, y cada fila necesita el nombre de su ciudad.
        var citiesById = await geographyLookup.FindCitiesAsync(
            customer.Addresses.Select(address => address.CityId).Distinct().ToArray(),
            cancellationToken);
        var principal = customer.PrincipalAddress;
        CustomerCityRef? principalCity = null;
        if (principal is not null)
        {
            citiesById.TryGetValue(principal.CityId, out principalCity);
        }

        return new QuotationCustomerRef(
            customer.Id.Value,
            customer.TenantId,
            customer.Cuc,
            customer.IsActive,
            customer.Name,
            customer.Phone,
            // La direccion del cliente es la de su principal: es la que la cotizacion propone y
            // la que viaja en el WhatsApp.
            principal?.Address,
            customer.WithRetention,
            customer.VatSurplus,
            customer.Email,
            principalCity?.CityId,
            principalCity?.CityName,
            principalCity?.DepartmentId,
            principalCity?.DepartmentName,
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
