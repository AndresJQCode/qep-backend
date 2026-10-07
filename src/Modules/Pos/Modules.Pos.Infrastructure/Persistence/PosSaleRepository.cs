using Microsoft.EntityFrameworkCore;
using Modules.Pos.Application;
using Modules.Pos.Domain;

namespace Modules.Pos.Infrastructure.Persistence;

internal sealed class PosSaleRepository(PosDbContext dbContext) : IPosSaleRepository
{
    public Task<PosSale?> FindAsync(Guid tenantId, PosSaleId id, CancellationToken cancellationToken) =>
        dbContext.Sales.SingleOrDefaultAsync(
            sale => sale.TenantId == tenantId && sale.Id == id, cancellationToken);

    public void Add(PosSale sale) => dbContext.Sales.Add(sale);
}
