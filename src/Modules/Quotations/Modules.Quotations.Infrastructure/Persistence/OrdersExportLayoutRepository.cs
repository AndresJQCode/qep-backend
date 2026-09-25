using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class OrdersExportLayoutRepository(QuotationsDbContext dbContext) : IOrdersExportLayoutRepository
{
    // Rastreado: el handler llama Replace sobre la entidad y SaveChangesAsync reescribe el JSON.
    // Las columnas viven en la misma fila, así que no hay Include que hacer.
    public Task<OrdersExportLayout?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.OrdersExportLayouts.SingleOrDefaultAsync(
            layout => layout.TenantId == tenantId, cancellationToken);

    public void Add(OrdersExportLayout layout) => dbContext.OrdersExportLayouts.Add(layout);
}
