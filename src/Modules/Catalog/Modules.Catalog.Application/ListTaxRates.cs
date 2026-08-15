using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record ListTaxRatesQuery(Guid TenantId) : IQuery<IReadOnlyList<TaxRateDto>>;

public sealed class ListTaxRatesHandler(
    ITaxRateRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<ListTaxRatesQuery, IReadOnlyList<TaxRateDto>>
{
    public async Task<IReadOnlyList<TaxRateDto>> HandleAsync(
        ListTaxRatesQuery query,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CatalogPermissions.TaxRateRead);

        var taxRates = await repository.ListAsync(query.TenantId, cancellationToken);

        return taxRates.Select(taxRate => taxRate.ToDto()).ToArray();
    }
}
