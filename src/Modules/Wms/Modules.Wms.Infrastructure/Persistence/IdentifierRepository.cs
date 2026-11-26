using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed class IdentifierRepository(WmsDbContext dbContext) : IIdentifierRepository
{
    public void AddRange(IEnumerable<Identifier> identifiers) => dbContext.Identifiers.AddRange(identifiers);

    // Rastreados: quien los lista es quien los reemplaza (RemoveRange) al editar el perfil.
    public async Task<IReadOnlyList<Identifier>> ListByTargetAsync(Guid tenantId, Guid targetId, CancellationToken ct) =>
        await dbContext.Identifiers
            .Where(identifier => identifier.TenantId == tenantId && identifier.TargetId == targetId)
            .OrderByDescending(identifier => identifier.IsPrimary)
            .ThenBy(identifier => identifier.Value)
            .ToListAsync(ct);

    public void RemoveRange(IEnumerable<Identifier> identifiers) => dbContext.Identifiers.RemoveRange(identifiers);
}
