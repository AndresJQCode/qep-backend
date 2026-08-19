using BuildingBlocks.Application;
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

        return new CustomerPage(
            customers.Select(customer => customer.ToDto()).ToArray(),
            total,
            page,
            pageSize);
    }
}
