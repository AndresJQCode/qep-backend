using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Wholesale.Application;

public sealed record ListWholesaleCategoriesQuery(Guid TenantId) : IQuery<WholesaleCategoriesResponse>;

/// <summary>
/// Ruling W5: las categorías del mayorista para elegir la visibilidad de una lista. Mismo permiso que las listas y el mismo
/// puerto de lectura del catálogo que la vista previa: quien administra listas no necesita <c>catalog.product.read</c>.
/// </summary>
public sealed class ListWholesaleCategoriesHandler(
    IExecutionContext executionContext,
    IWholesaleCatalogReader catalog)
    : IQueryHandler<ListWholesaleCategoriesQuery, WholesaleCategoriesResponse>
{
    public async Task<WholesaleCategoriesResponse> HandleAsync(ListWholesaleCategoriesQuery query, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, query.TenantId, WholesalePermissions.PriceListManage);
        var nodes = await catalog.ListCategoriesAsync(query.TenantId, cancellationToken);

        return new WholesaleCategoriesResponse(nodes
            .Select(node => new WholesaleCategoryDto(node.Id, node.Name, node.ParentId))
            .ToList());
    }
}
