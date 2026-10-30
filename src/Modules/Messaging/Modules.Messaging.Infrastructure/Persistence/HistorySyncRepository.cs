using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-29 §5: el agregado con tracking; <c>version</c> es el token de concurrencia, así que un cambio del worker entre
/// la lectura y el guardado sale como <c>concurrency.conflict</c> (<see cref="MessagingSaveChanges"/>). <c>claimed_until</c> es del
/// worker y nunca se escribe desde acá.</summary>
internal sealed class HistorySyncRepository(MessagingDbContext dbContext) : IHistorySyncRepository
{
    public Task<HistorySync?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.HistorySyncs.SingleOrDefaultAsync(
            sync => sync.TenantId == tenantId && sync.ConnectionId == connectionId, cancellationToken);
}
