using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Suppliers.Application;

/// <summary>
/// <c>Status</c> llega como texto para que un valor raro sea un 422 con su código
/// (<c>status_filter_invalid</c>) y no un 400 del binding. Ausente = activos e inactivos.
/// </summary>
public sealed record ListSuppliersQuery(
    Guid TenantId,
    string? Search,
    string? Status,
    string? Category,
    int Page,
    int PageSize) : IQuery<SupplierPage>;

/// <summary>Una página de la lista con el total que la grilla necesita para paginar.</summary>
public sealed record SupplierPage(
    IReadOnlyList<SupplierListItemResponse> Items,
    int Total,
    int Page,
    int PageSize);

public static class SupplierPaging
{
    public const int DefaultPageSize = 50;

    /// <summary>
    /// Tope duro: el tamaño de página lo elige el cliente, y sin límite un <c>?pageSize=1000000</c>
    /// trae el tenant entero a memoria. Se recorta en silencio y no se falla: pedir de más es una
    /// expectativa que el servidor no puede cumplir, no un cuerpo mal escrito, y la respuesta lleva
    /// el <c>PageSize</c> real.
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

public sealed class ListSuppliersHandler(
    ISupplierRepository repository,
    ISupplierGeographyLookup geographyLookup,
    IExecutionContext executionContext)
    : IQueryHandler<ListSuppliersQuery, SupplierPage>
{
    public async Task<SupplierPage> HandleAsync(
        ListSuppliersQuery query,
        CancellationToken cancellationToken)
    {
        SuppliersAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, SuppliersPermissions.Read);

        var status = SupplierStatusFilterParser.Parse(query.Status);
        var page = SupplierPaging.NormalizePage(query.Page);
        var pageSize = SupplierPaging.NormalizePageSize(query.PageSize);

        var (suppliers, total) = await repository.SearchAsync(
            query.TenantId, query.Search, status, query.Category, page, pageSize, cancellationToken);

        // Las ciudades de la página, en una sola consulta en lote y no una por proveedor (hasta 200
        // por página serían 200 consultas de más).
        var cityIds = suppliers
            .Where(supplier => supplier.CityId is not null)
            .Select(supplier => supplier.CityId!.Value)
            .Distinct()
            .ToArray();
        var citiesById = cityIds.Length == 0
            ? new Dictionary<Guid, SupplierCityRef>()
            : await geographyLookup.FindCitiesAsync(cityIds, cancellationToken);

        var items = suppliers
            .Select(supplier => supplier.ToListItem(ResolveCity(citiesById, supplier)))
            .ToArray();

        return new SupplierPage(items, total, page, pageSize);
    }

    // Un miss es corrupción de datos y no una entrada inválida: mismo criterio que ToResponseAsync.
    private static SupplierCityRef? ResolveCity(
        IReadOnlyDictionary<Guid, SupplierCityRef> citiesById,
        Domain.Supplier supplier) =>
        supplier.CityId is not { } cityId
            ? null
            : citiesById.TryGetValue(cityId, out var city)
                ? city
                : throw new InvalidOperationException(
                    $"City '{cityId}' referenced by supplier '{supplier.Id}' was not found.");
}
