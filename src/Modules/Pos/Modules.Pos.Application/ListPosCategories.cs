using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>
/// Los chips de categoría de la caja (spec 2026-10-26 §3.4). Con <c>pos.sale.create</c>, el mismo permiso
/// de la cuadrícula que filtran: el cajero no tiene <c>catalog.product.read</c> (spec §3.5).
/// </summary>
public sealed record ListPosCategoriesQuery(Guid TenantId) : IQuery<PosCategoriesResponse>;

public sealed class ListPosCategoriesHandler(IPosCategoryLookup categories, IExecutionContext executionContext)
    : IQueryHandler<ListPosCategoriesQuery, PosCategoriesResponse>
{
    public async Task<PosCategoriesResponse> HandleAsync(ListPosCategoriesQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.SaleCreate);
        var list = await categories.ListAsync(query.TenantId, cancellationToken);
        return new PosCategoriesResponse(
            list.Items.Select(item => new PosCategoryResponse(
                item.Id, item.Name, item.ParentId, item.ProductCount, item.QuickAccessPosition)).ToArray(),
            list.Total);
    }
}
