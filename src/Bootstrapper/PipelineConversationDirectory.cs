using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-13 D9 y §5.3: las conversaciones que Pipeline necesita, leídas del <c>DbContext</c> de Messaging como
/// <see cref="QuotationsReportSource"/> lee el de Quotations. Sin tracking: Pipeline nunca escribe en Messaging. Usa
/// <c>IX_conversations_tenant_open</c> y <c>IX_conversations_tenant_customer_activity</c>.
/// </summary>
internal sealed class PipelineConversationDirectory(MessagingDbContext messaging) : IPipelineConversationDirectory
{
    /// <summary>
    /// D37 y P16 del plan: el tope y el <c>Count</c> real salen de la consulta. Para eso la pendencia de §5.1 va en el
    /// <c>WHERE</c>: es <see cref="PipelineRules.IsPending"/> escrita en SQL, con dos <c>NOT EXISTS</c> sobre las tablas de Pipeline en
    /// la misma base (Bootstrapper es la raíz de composición; ningún módulo se referencia). Si una de las dos cambia, cambia la
    /// otra: <c>PipelineRulesTests</c> fija la regla y <c>OpenConversationsAreThePendingOnesCappedWithTheRealCount</c> y
    /// <c>TriageApiTests</c>, esta consulta. <c>count(*) OVER ()</c> se calcula antes del <c>LIMIT</c>: es el total.
    /// </summary>
    public async Task<PipelineConversationPage> ListOpenAsync(Guid tenantId, int limit, CancellationToken cancellationToken)
    {
        var noInbound = TriageDecision.NoInbound;
        var open = ConversationStatus.Open.ToString();
        var rows = await messaging.Database.SqlQuery<OpenConversationRow>($"""
            SELECT c.id AS "Id", c.customer_id AS "CustomerId", c.last_inbound_at AS "LastInboundAt",
                   c.last_message_at AS "LastMessageAt", count(*) OVER () AS "Total"
            FROM messaging.conversations AS c
            WHERE c.tenant_id = {tenantId}
              AND c.status = {open}
              AND c.customer_id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM pipeline.deals AS d
                  WHERE d.tenant_id = c.tenant_id AND d.customer_id = c.customer_id AND d.closed_at IS NULL)
              AND NOT EXISTS (
                  SELECT 1 FROM pipeline.triage_decisions AS t
                  WHERE t.tenant_id = c.tenant_id AND t.conversation_id = c.id
                    AND t.decided_against_inbound_at >= COALESCE(c.last_inbound_at, {noInbound}))
            ORDER BY (c.last_inbound_at IS NOT NULL) DESC, c.last_inbound_at DESC, c.id
            LIMIT {limit}
            """).ToListAsync(cancellationToken);

        return new PipelineConversationPage(
            rows.Count == 0 ? 0 : (int)rows[0].Total,
            [.. rows.Select(row => new PipelineConversation(row.Id, row.CustomerId, true, row.LastInboundAt, row.LastMessageAt))]);
    }

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

    // Clase con init y no un record: EF no ve a través del constructor de un record en SqlQuery (QuotationsReportSource).
    private sealed class OpenConversationRow
    {
        public Guid Id { get; init; }

        public Guid CustomerId { get; init; }

        public DateTimeOffset? LastInboundAt { get; init; }

        public DateTimeOffset? LastMessageAt { get; init; }

        public long Total { get; init; }
    }
}
