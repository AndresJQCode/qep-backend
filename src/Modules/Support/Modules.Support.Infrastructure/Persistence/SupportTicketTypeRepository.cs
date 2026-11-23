using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

internal sealed class SupportTicketTypeRepository(SupportDbContext dbContext) : ISupportTicketTypeRepository
{
    public async Task<IReadOnlyList<SupportTicketType>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.TicketTypes
            .Where(type => type.TenantId == tenantId && !type.IsInternal)
            .OrderBy(type => type.Position)
            .ThenBy(type => type.Id)
            .ToListAsync(cancellationToken);

    public Task<SupportTicketType?> FindInternalAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.TicketTypes.FirstOrDefaultAsync(type => type.TenantId == tenantId && type.IsInternal, cancellationToken);

    public Task<SupportTicketType?> FindAsync(Guid tenantId, SupportTicketTypeId id, CancellationToken cancellationToken) =>
        dbContext.TicketTypes.FirstOrDefaultAsync(type => type.TenantId == tenantId && type.Id == id, cancellationToken);

    public void Add(SupportTicketType type) => dbContext.TicketTypes.Add(type);

    public void Remove(SupportTicketType type) => dbContext.TicketTypes.Remove(type);
}
