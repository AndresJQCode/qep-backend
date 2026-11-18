using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Wholesale.Application;

public sealed record ListPriceListsQuery(Guid TenantId) : IQuery<PriceListsResponse>;

/// <summary>
/// Todas las listas por nombre con sus categorías y su conteo de distribuidores. Los nombres de las categorías de todas las
/// listas salen de una sola lectura del catálogo (ruling W4), nunca una por lista.
/// </summary>
public sealed class ListPriceListsHandler(
    IExecutionContext executionContext,
    IPriceListRepository priceLists,
    IDistributorUsage usage,
    IWholesaleCatalogReader catalog)
    : IQueryHandler<ListPriceListsQuery, PriceListsResponse>
{
    public async Task<PriceListsResponse> HandleAsync(ListPriceListsQuery query, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, query.TenantId, WholesalePermissions.PriceListManage);
        var lists = await priceLists.ListAsync(query.TenantId, cancellationToken);

        var categoryIds = lists.SelectMany(list => list.CategoryIds).Distinct().ToArray();
        IReadOnlyDictionary<Guid, WholesaleCategoryRef> categories = categoryIds.Length == 0
            ? new Dictionary<Guid, WholesaleCategoryRef>()
            : await catalog.FindCategoriesAsync(query.TenantId, categoryIds, cancellationToken);
        var counts = await usage.CountByPriceListAsync(query.TenantId, cancellationToken);

        return new PriceListsResponse(lists
            .OrderBy(list => list.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(list => list.Id.Value)
            .Select(list => PriceListMapping.ToSummary(list, categories, counts.GetValueOrDefault(list.Id)))
            .ToList());
    }
}
