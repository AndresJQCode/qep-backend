using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record GetTaxRateQuery(Guid TenantId, Guid TaxRateId) : IQuery<TaxRateDto>;

public sealed class GetTaxRateHandler(
    ITaxRateRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<GetTaxRateQuery, TaxRateDto>
{
    public async Task<TaxRateDto> HandleAsync(
        GetTaxRateQuery query,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CatalogPermissions.TaxRateRead);

        var taxRate = await repository.FindAsync(
            query.TenantId, new TaxRateId(query.TaxRateId), cancellationToken);

        return taxRate is null
            ? throw TaxRateNotFound.For(query.TaxRateId)
            : taxRate.ToDto();
    }
}
