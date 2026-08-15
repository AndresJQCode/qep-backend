using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record GetProductQuery(Guid TenantId, Guid ProductId) : IQuery<ProductDto>;

public sealed class GetProductHandler(
    IProductRepository repository,
    IProductImageLookup imageLookup,
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

        if (product is null)
        {
            throw ProductNotFound.For(query.ProductId);
        }

        // Un solo producto pasa igual por el mapeo de lote: la lista de ids tiene un elemento, o
        // ninguno si no hay portada, y en ese caso no se le pregunta nada a Storage.
        var dtos = await new[] { product }.ToDtosAsync(imageLookup, query.TenantId, cancellationToken);
        return dtos[0];
    }
}
