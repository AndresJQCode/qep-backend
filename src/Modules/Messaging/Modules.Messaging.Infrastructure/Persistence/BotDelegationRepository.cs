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

    // Por IX_bot_delegations_tenant_conversation. Npgsql sólo escribe un timestamptz desde un DateTimeOffset en UTC.
    public Task<int> InterruptLiveAsync(Guid tenantId, Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var endedAt = now.ToUniversalTime();
        return dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.bot_delegations SET status = 'Interrupted', ended_at = {endedAt}
             WHERE tenant_id = {tenantId} AND conversation_id = {conversationId} AND status = 'Live'
            """,
            cancellationToken);
    }
}
