using BuildingBlocks.Application;
using Modules.Pricing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pricing.Application;

public sealed record GetPriceListQuery(Guid TenantId, Guid PriceListId) : IQuery<PriceListDto>;

public sealed class GetPriceListHandler(
    IPriceListRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<GetPriceListQuery, PriceListDto>
{
    public async Task<PriceListDto> HandleAsync(
        GetPriceListQuery query,
        CancellationToken cancellationToken)
    {
        PricingAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, PricingPermissions.PriceListRead);

        var priceList = await repository.FindAsync(
            query.TenantId, new PriceListId(query.PriceListId), cancellationToken);

        return priceList is null
            ? throw PriceListNotFound.For(query.PriceListId)
            : priceList.ToDto();
    }
}
