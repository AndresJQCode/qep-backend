using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

public sealed record GetPriceListQuery(Guid TenantId, Guid PriceListId) : IQuery<PriceListDetailDto>;

/// <summary>El detalle con las categorías que siguen existiendo (D-23) y cuántos distribuidores la usan.</summary>
public sealed class GetPriceListHandler(
    IExecutionContext executionContext,
    IPriceListRepository priceLists,
    IDistributorUsage usage,
    IWholesaleCatalogReader catalog)
    : IQueryHandler<GetPriceListQuery, PriceListDetailDto>
{
    public async Task<PriceListDetailDto> HandleAsync(GetPriceListQuery query, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, query.TenantId, WholesalePermissions.PriceListManage);
        var list = await priceLists.GetAsync(query.TenantId, new PriceListId(query.PriceListId), cancellationToken)
            ?? throw WholesaleConflicts.PriceListNotFound(query.PriceListId);

        IReadOnlyDictionary<Guid, WholesaleCategoryRef> categories = list.CategoryIds.Count == 0
            ? new Dictionary<Guid, WholesaleCategoryRef>()
            : await catalog.FindCategoriesAsync(query.TenantId, list.CategoryIds, cancellationToken);
        var counts = await usage.CountByPriceListAsync(query.TenantId, cancellationToken);

        return PriceListMapping.ToDetail(list, categories, counts.GetValueOrDefault(list.Id));
    }
}
