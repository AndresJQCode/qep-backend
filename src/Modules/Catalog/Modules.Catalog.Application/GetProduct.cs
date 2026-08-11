using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record GetProductQuery(Guid TenantId, Guid ProductId) : IQuery<ProductDto>;

public sealed class GetProductHandler(
    IProductRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<GetProductQuery, ProductDto>
{
    public async Task<ProductDto> HandleAsync(
        GetProductQuery query,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CatalogPermissions.ProductRead);

        var product = await repository.FindAsync(
            query.TenantId, new ProductId(query.ProductId), cancellationToken);

        return product is null
            ? throw ProductNotFound.For(query.ProductId)
            : product.ToDto();
    }
}
