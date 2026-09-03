using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record GetQuotationQuery(Guid TenantId, Guid QuotationId) : IQuery<QuotationDto>;

public sealed class GetQuotationHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationCustomerLookup customerLookup,
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

        // Retención/excedente de IVA son hechos del cliente, no una foto congelada a
        // propósito (ver Quotation.RefreshCustomerTaxProfile) — mientras la cotización sigue
        // editable, cada lectura la deja al día con el cliente maestro en vez de esperar a la
        // próxima edición de línea para notarlo.
        var customer = await customerLookup.FindAsync(
            query.TenantId, quotation.ClientId, cancellationToken);
        if (customer is not null)
        {
            var before = (quotation.CustomerWithRetention, quotation.CustomerVatSurplus);
            quotation.RefreshCustomerTaxProfile(customer.WithRetention, customer.VatSurplus);
            if (before != (quotation.CustomerWithRetention, quotation.CustomerVatSurplus))
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }

        return quotation.ToDto();
    }
}
