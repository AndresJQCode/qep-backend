using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record GetQuotationQuery(Guid TenantId, Guid QuotationId) : IQuery<QuotationDto>;

public sealed class GetQuotationHandler(
    IQuotationRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<GetQuotationQuery, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        GetQuotationQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, QuotationsPermissions.QuotationRead);

        var quotation = await repository.FindAsync(
            query.TenantId, new QuotationId(query.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(query.QuotationId);

        // Una lectura pura (spec impuestos por tipo §7.6): la cotización congela las tasas y el excedente de
        // IVA del cliente, así que ya no hay nada que refrescar contra el cliente maestro al abrirla.
        return quotation.ToDto();
    }
}
