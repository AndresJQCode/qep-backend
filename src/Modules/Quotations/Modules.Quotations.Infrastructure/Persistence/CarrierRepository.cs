using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class CarrierRepository(QuotationsDbContext dbContext) : ICarrierRepository
{
    // Rastreadas: reordenar muta varias y SaveChangesAsync las persiste. Los nombres son owned y
    // vienen con la transportadora, sin Include.
    public async Task<IReadOnlyList<Carrier>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Carriers
            .Where(carrier => carrier.TenantId == tenantId)
            .OrderBy(carrier => carrier.SortOrder)
            .ToListAsync(cancellationToken);

    public Task<Carrier?> FindAsync(Guid tenantId, CarrierId id, CancellationToken cancellationToken) =>
        dbContext.Carriers.SingleOrDefaultAsync(
            carrier => carrier.TenantId == tenantId && carrier.Id == id, cancellationToken);

    public void Add(Carrier carrier) => dbContext.Carriers.Add(carrier);
}
