using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Modules.Pipeline.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-13 D9 y §5.3: las conversaciones que Pipeline necesita, leídas del <c>DbContext</c> de Messaging como
/// <see cref="QuotationsReportSource"/> lee el de Quotations. Sin tracking: Pipeline nunca escribe en Messaging. Usa
/// <c>IX_conversations_tenant_customer_activity</c>.
/// </summary>
internal sealed class PipelineConversationDirectory(MessagingDbContext messaging) : IPipelineConversationDirectory
{
    public async Task<IReadOnlyDictionary<Guid, PipelineCustomerActivity>> FindActivityAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        if (customerIds.Count == 0)
        {
            return new Dictionary<Guid, PipelineCustomerActivity>();
        }

        var ids = customerIds.ToArray();
        var rows = await messaging.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId
                && conversation.CustomerId != null
                && ids.Contains(conversation.CustomerId.Value))
            .OrderByDescending(conversation => conversation.LastActivityAt)
            .Select(conversation => new { CustomerId = conversation.CustomerId!.Value, conversation.Id, conversation.LastMessageAt })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.CustomerId)
            .ToDictionary(
                group => group.Key,
                group => new PipelineCustomerActivity(
                    group.Key, group.Max(row => row.LastMessageAt), group.Select(row => row.Id).ToArray()));
    }

    public Task<PipelineConversation?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        messaging.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId && conversation.Id == conversationId)
            .Select(conversation => new PipelineConversation(
                conversation.Id,
                conversation.CustomerId,
                conversation.Status == ConversationStatus.Open,
                conversation.LastInboundAt,
                conversation.LastMessageAt))
            .SingleOrDefaultAsync(cancellationToken);
}
