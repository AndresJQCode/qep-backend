using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed class OwnerRepository(WmsDbContext dbContext) : IOwnerRepository
{
    public void Add(Owner owner) => dbContext.Owners.Add(owner);

    public Task<Owner?> FindAsync(Guid tenantId, OwnerId id, CancellationToken ct) =>
        dbContext.Owners.FirstOrDefaultAsync(owner => owner.TenantId == tenantId && owner.Id == id, ct);

    // No crea nada: sólo IWmsProvisioner escribe el Self (S5). Que falte es un bug del llamador, que no provisionó antes, y no una
    // condición del usuario: por eso lanza y no devuelve null.
    public async Task<Owner> GetSelfAsync(Guid tenantId, CancellationToken ct) =>
        await dbContext.Owners.FirstOrDefaultAsync(owner => owner.TenantId == tenantId && owner.Kind == OwnerKind.Self, ct)
            ?? throw new InvalidOperationException($"Tenant '{tenantId}' has no self owner: IWmsProvisioner.EnsureAsync was not called first.");

    public async Task<IReadOnlyList<Owner>> ListAsync(Guid tenantId, CancellationToken ct) =>
        await dbContext.Owners
            .AsNoTracking()
            .Where(owner => owner.TenantId == tenantId)
            .OrderBy(owner => owner.Kind == OwnerKind.Self ? 0 : 1)
            .ThenBy(owner => owner.Name)
            .ToListAsync(ct);
}
