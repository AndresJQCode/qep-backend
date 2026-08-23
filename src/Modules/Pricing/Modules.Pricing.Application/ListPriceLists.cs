using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Pricing.Application;

public sealed record ListPriceListsQuery(Guid TenantId)
    : IQuery<IReadOnlyList<PriceListDto>>;

public sealed class ListPriceListsHandler(
    IPriceListRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<ListPriceListsQuery, IReadOnlyList<PriceListDto>>
{
    public async Task<IReadOnlyList<PriceListDto>> HandleAsync(
        ListPriceListsQuery query,
        CancellationToken cancellationToken)
    {
        PricingAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, PricingPermissions.PriceListRead);

        var priceLists = await repository.ListAsync(query.TenantId, cancellationToken);

        return priceLists.Select(priceList => priceList.ToDto()).ToArray();
    }
}
