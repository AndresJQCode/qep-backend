using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

internal sealed class SupportUnclassifiedRepository(SupportDbContext dbContext) : ISupportUnclassifiedRepository
{
    public void Add(SupportUnclassifiedMessage message) => dbContext.UnclassifiedMessages.Add(message);

    public Task<SupportUnclassifiedMessage?> FindAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
        dbContext.UnclassifiedMessages.FirstOrDefaultAsync(
            item => item.TenantId == tenantId && item.MessageId == messageId, cancellationToken);

    // Las dos usan IX_unclassified_pending: (tenant_id, conversation_id) WHERE resolution IS NULL.
    public async Task<IReadOnlyList<SupportUnclassifiedMessage>> ListPendingAsync(
        Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        await Pending(tenantId, conversationId)
            .OrderBy(item => item.OccurredAt)
            .ThenBy(item => item.MessageId)
            .ToListAsync(cancellationToken);

    public Task<int> CountPendingAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        Pending(tenantId, conversationId).CountAsync(cancellationToken);

    private IQueryable<SupportUnclassifiedMessage> Pending(Guid tenantId, Guid conversationId) =>
        dbContext.UnclassifiedMessages.Where(item =>
            item.TenantId == tenantId && item.ConversationId == conversationId && item.Resolution == null);
}
