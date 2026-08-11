using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record ListProductsQuery(Guid TenantId, string? Search)
    : IQuery<IReadOnlyList<ProductDto>>;

public sealed class ListProductsHandler(
    IProductRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<ListProductsQuery, IReadOnlyList<ProductDto>>
{
    public async Task<IReadOnlyList<ProductDto>> HandleAsync(
        ListProductsQuery query,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CatalogPermissions.ProductRead);

        var products = await repository.SearchAsync(
            query.TenantId, query.Search, cancellationToken);

        return products.Select(product => product.ToDto()).ToArray();
    }
}
