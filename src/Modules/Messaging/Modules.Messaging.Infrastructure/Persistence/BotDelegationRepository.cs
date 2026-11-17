using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-11-17 §5.3: sin token de concurrencia propio. Toda escritura de una delegación viaja con un cambio de la conversación
/// (versión) o con un <c>UPDATE … WHERE status = 'Live'</c>, así que dos terminaciones a la vez chocan en la conversación.</summary>
internal sealed class BotDelegationRepository(MessagingDbContext dbContext) : IBotDelegationRepository
{
    public Task<BotDelegation?> FindAsync(Guid tenantId, Guid delegationId, CancellationToken cancellationToken) =>
        dbContext.BotDelegations.SingleOrDefaultAsync(
            delegation => delegation.TenantId == tenantId && delegation.Id == delegationId, cancellationToken);

    public void Add(BotDelegation delegation) => dbContext.BotDelegations.Add(delegation);
}
