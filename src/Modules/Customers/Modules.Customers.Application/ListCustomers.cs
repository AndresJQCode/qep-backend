using BuildingBlocks.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record ListCustomersQuery(
    Guid TenantId,
    string? Search,
    int Page,
    int PageSize) : IQuery<CustomerPage>;

/// <summary>Una pagina de clientes con el total que la UI necesita para paginar.</summary>
public sealed record CustomerPage(
    IReadOnlyList<CustomerDto> Items,
    int Total,
    int Page,
    int PageSize);

public static class CustomerPaging
{
    public const int DefaultPageSize = 50;

    /// <summary>
    /// Tope duro. El tamano de pagina lo elige el cliente, asi que sin limite un
    /// <c>?pageSize=1000000</c> se traduce en traerse el tenant entero a memoria — un DoS que se
    /// escribe desde la barra de direcciones.
    ///
    /// Recortar en silencio y no fallar es deliberado: pedir mas de lo permitido no es un cuerpo
    /// mal escrito, es una expectativa que el servidor no puede cumplir, y devolver la pagina
    /// maxima es mas util que un 422. La respuesta lleva el <c>PageSize</c> real, asi que el
    /// llamador puede ver que se le recorto.
    /// </summary>
    public const int MaxPageSize = 200;

    public static int NormalizePage(int page) => page < 1 ? 1 : page;

    public static int NormalizePageSize(int pageSize) => pageSize switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => pageSize
    };
}

public sealed class ListCustomersHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerGeographyLookup geographyLookup,
    IExecutionContext executionContext)
    : IQueryHandler<ListCustomersQuery, CustomerPage>
{
    public async Task<CustomerPage> HandleAsync(
        ListCustomersQuery query,
        CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CustomersPermissions.CustomerRead);

        var page = CustomerPaging.NormalizePage(query.Page);
        var pageSize = CustomerPaging.NormalizePageSize(query.PageSize);

        var (customers, total) = await repository.SearchAsync(
            query.TenantId, query.Search, page, pageSize, cancellationToken);

        var items = await ToDtosAsync(query.TenantId, customers, cancellationToken);

        return new CustomerPage(items, total, page, pageSize);
    }

    // Resuelve las ciudades y las clasificaciones distintas de la pagina con una sola consulta en
    // lote cada una, en vez de una por cliente: hasta 200 clientes por pagina
    // (CustomerPaging.MaxPageSize) son hasta 200 consultas de mas si esto fuera un FindAsync por
    // fila, y ese N+1 es exactamente lo que ICustomerGeographyLookup.FindCitiesAsync y
    // IClientClassificationRepository.ListByIdsAsync existen para evitar.
    private async Task<IReadOnlyList<CustomerDto>> ToDtosAsync(
        Guid tenantId,
        IReadOnlyList<Customer> customers,
        CancellationToken cancellationToken)
    {
        if (customers.Count == 0)
        {
            return [];
        }

        var cityIds = customers.Select(customer => customer.CityId).Distinct().ToArray();
        var classificationIds = customers
            .Select(customer => customer.ClassificationId)
            .Distinct()
            .ToArray();

        var citiesById = await geographyLookup.FindCitiesAsync(cityIds, cancellationToken);
        var classifications = await classificationRepository.ListByIdsAsync(
            tenantId, classificationIds, cancellationToken);
        var classificationsById = classifications.ToDictionary(
            classification => classification.Id);

        var items = new List<CustomerDto>(customers.Count);
        foreach (var customer in customers)
        {
            // La FK de base garantiza que las dos referencias existan: un miss aca es corrupcion
            // de datos, no una entrada de usuario invalida. Ver CustomerMapping.ToDtoAsync.
            var city = citiesById.TryGetValue(customer.CityId, out var cityRef)
                ? cityRef
                : throw new InvalidOperationException(
                    $"City '{customer.CityId}' referenced by customer '{customer.Id}' " +
                    "was not found.");
            var classification = classificationsById.TryGetValue(
                customer.ClassificationId, out var classificationValue)
                ? classificationValue
                : throw new InvalidOperationException(
                    $"Classification '{customer.ClassificationId}' referenced by customer " +
                    $"'{customer.Id}' was not found.");

            items.Add(customer.ToDto(city, classification));
        }

        return items;
    }
}
