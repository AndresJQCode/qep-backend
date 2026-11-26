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

    public async Task<IReadOnlyList<Guid>> ListOpenOrderIdsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return [];
        }

        return await OpenItems(tenantId)
            .Where(item => orderIds.Contains(item.OrderId))
            .Select(item => item.OrderId)
            .ToListAsync(cancellationToken);
    }

    public Task<Guid?> FindOpenBatchIdAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        OpenItems(tenantId)
            .Where(item => item.OrderId == orderId)
            .Select(item => (Guid?)item.BatchId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<(IReadOnlyList<SiigoInvoiceBatch> Items, int Total)> SearchAsync(
        Guid tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var batches = dbContext.InvoiceBatches.AsNoTracking().Where(batch => batch.TenantId == tenantId);
        var total = await batches.CountAsync(cancellationToken);
        var items = await batches
            .OrderByDescending(batch => batch.CreatedAt)
            .ThenByDescending(batch => batch.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(batch => batch.Items)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    // Los ítems sin terminar: a lo sumo uno por pedido (IX_invoice_batch_items_tenant_order_open).
    private IQueryable<SiigoInvoiceBatchItem> OpenItems(Guid tenantId) =>
        dbContext.InvoiceBatchItems
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId
                && (item.Status == SiigoInvoiceBatchItemStatus.Pending || item.Status == SiigoInvoiceBatchItemStatus.Sending));
}
