using Microsoft.EntityFrameworkCore;
using Modules.Siigo.Application;
using Modules.Siigo.Domain;

namespace Modules.Siigo.Infrastructure.Persistence;

/// <summary>Con tracking: el llamador modifica y guarda con <c>ISiigoUnitOfWork</c>. Todo filtra por <c>tenant_id</c>.</summary>
internal sealed class SiigoInvoiceRepository(SiigoDbContext dbContext) : ISiigoInvoiceRepository
{
    public async Task<IReadOnlyList<SiigoInvoice>> ListByOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        await dbContext.Invoices
            .Where(invoice => invoice.TenantId == tenantId && invoice.OrderId == orderId)
            .OrderBy(invoice => invoice.CreatedAt)
            .ThenBy(invoice => invoice.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SiigoInvoice>> ListByIdsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
        {
            return [];
        }

        return await dbContext.Invoices
            .Where(invoice => invoice.TenantId == tenantId && invoiceIds.Contains(invoice.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SiigoInvoice>> ListFreightAsync(
        Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return [];
        }

        return await dbContext.Invoices
            .Where(invoice => invoice.TenantId == tenantId && orderIds.Contains(invoice.OrderId)
                && invoice.Kind == SiigoInvoiceKind.Freight)
            .ToListAsync(cancellationToken);
    }

    public void Add(SiigoInvoice invoice) => dbContext.Invoices.Add(invoice);
}
