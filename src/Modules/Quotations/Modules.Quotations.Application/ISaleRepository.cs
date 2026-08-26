using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

public interface ISaleRepository
{
    Task<Sale?> FindByQuotationIdAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken);

    void Add(Sale sale);
}
