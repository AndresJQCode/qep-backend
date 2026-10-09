using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record GetCurrenciesInUseQuery(Guid TenantId) : IQuery<IReadOnlyList<string>>;

public sealed class GetCurrenciesInUseHandler(
    ICurrenciesInUse currenciesInUse,
    IExecutionContext executionContext)
    : IQueryHandler<GetCurrenciesInUseQuery, IReadOnlyList<string>>
{
    public Task<IReadOnlyList<string>> HandleAsync(
        GetCurrenciesInUseQuery query, CancellationToken cancellationToken)
    {
        // The double layer every catalog read has: route policy plus this revalidation (403, never 404).
        CatalogAuthorization.EnsureAuthorized(executionContext, query.TenantId, CatalogPermissions.ProductRead);
        return currenciesInUse.GetAsync(query.TenantId, cancellationToken);
    }
}
