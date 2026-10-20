using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record GetProductQuery(Guid TenantId, Guid ProductId) : IQuery<ProductDto>;

public sealed class GetProductHandler(
    IProductRepository repository,
    IProductImageLookup imageLookup,
    IOptionDefinitionRepository optionRepository,
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

        // Un solo producto pasa igual por el mapeo de lote: una consulta para las fotos de la
        // galería (ninguna si no tiene) y otra para los nombres de sus opciones (ninguna si no
        // tiene opciones).
        var dtos = await new[] { product }.ToDtosAsync(
            imageLookup, optionRepository, query.TenantId, cancellationToken);
        return dtos[0];
    }
}
