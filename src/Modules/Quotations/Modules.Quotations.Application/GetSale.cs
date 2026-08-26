using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record GetSaleQuery(Guid TenantId, Guid QuotationId) : IQuery<SaleDto>;

public sealed class GetSaleHandler(
    ISaleRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<GetSaleQuery, SaleDto>
{
    public async Task<SaleDto> HandleAsync(GetSaleQuery query, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, SalesPermissions.SaleRead);

        var sale = await repository.FindByQuotationIdAsync(
            query.TenantId, new QuotationId(query.QuotationId), cancellationToken);

        return sale?.ToDto() ?? throw SaleNotFound.For(query.QuotationId);
    }
}
