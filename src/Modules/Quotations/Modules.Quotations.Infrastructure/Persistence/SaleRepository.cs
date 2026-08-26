using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class SaleRepository(QuotationsDbContext dbContext) : ISaleRepository
{
    public Task<Sale?> FindByQuotationIdAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        dbContext.Sales
            .Include(sale => sale.PaymentProofs)
            .SingleOrDefaultAsync(
                sale => sale.TenantId == tenantId && sale.QuotationId == quotationId,
                cancellationToken);

    public void Add(Sale sale) => dbContext.Sales.Add(sale);
}
