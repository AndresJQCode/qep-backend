using Microsoft.EntityFrameworkCore;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;

namespace Modules.Siigo.Infrastructure.Persistence;

/// <summary>Con tracking: el llamador modifica y guarda con <c>ISiigoUnitOfWork</c>. Todo filtra por <c>tenant_id</c>.</summary>
internal sealed class SiigoInvoiceBatchRepository(SiigoDbContext dbContext) : ISiigoInvoiceBatchRepository
{
    public void Add(SiigoInvoiceBatch batch) => dbContext.InvoiceBatches.Add(batch);

    public Task<SiigoInvoiceBatch?> FindAsync(Guid tenantId, Guid batchId, CancellationToken cancellationToken) =>
        dbContext.InvoiceBatches
            .Include(batch => batch.Items)
            .FirstOrDefaultAsync(batch => batch.TenantId == tenantId && batch.Id == batchId, cancellationToken);
}
