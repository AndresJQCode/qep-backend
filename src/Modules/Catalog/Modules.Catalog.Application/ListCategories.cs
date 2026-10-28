using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record ListCategoriesQuery(Guid TenantId) : IQuery<IReadOnlyList<CategoryDto>>;

public sealed class ListCategoriesHandler(
    IProductCategoryRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<ListCategoriesQuery, IReadOnlyList<CategoryDto>>
{
    public async Task<IReadOnlyList<CategoryDto>> HandleAsync(
        ListCategoriesQuery query,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CatalogPermissions.ProductRead);

        // El catálogo cuenta todos los productos directos, activos o no.
        var items = await repository.ListAsync(
            query.TenantId, onlyActiveProducts: false, cancellationToken);

        return items.Select(item => item.ToDto()).ToArray();
    }
}
