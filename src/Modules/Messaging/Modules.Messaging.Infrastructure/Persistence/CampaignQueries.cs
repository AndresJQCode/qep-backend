using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-10-11 §6.2: la lista, el detalle y los destinatarios. Los contadores salen de una sola pasada por
/// <c>campaign_recipients</c> con <c>FILTER</c>, y siempre completos (una campaña sin destinatarios devuelve ceros, no nada). Entregado y
/// leído salen del <c>status</c> del mensaje atado por <c>message_id</c>, que ya mantiene el webhook; respondido es que haya un entrante
/// en la conversación del destinatario después de su <c>sent_at</c> (por <c>IX_messages_thread</c>).
/// </summary>
internal sealed class CampaignQueries(MessagingDbContext dbContext) : ICampaignQueries
{
    // MessageStatus como smallint: Sent = 1, Delivered = 2, Read = 3, Failed = 4 (MessageColumnCodes).
    private const short Delivered = 2;
    private const short Read = 3;

    public async Task<(IReadOnlyList<CampaignRow> Items, int Total)> ListAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var skip = (page - 1) * pageSize;
        var campaigns = await dbContext.Database.SqlQuery<CampaignColumns>(
            $"""
            SELECT c.id AS "Id", c.connection_id AS "ConnectionId", c.name AS "Name", c.template_name AS "TemplateName", c.template_language AS "TemplateLanguage",
                   c.template_category AS "TemplateCategory", c.parameters::text AS "Parameters", c.audience_tag_ids AS "AudienceTagIds",
                   c.audience_customer_ids AS "AudienceCustomerIds", c.status AS "Status", c.failure_reason AS "FailureReason", c.failure_code AS "FailureCode",
                   c.scheduled_at AS "ScheduledAt", c.started_at AS "StartedAt", c.completed_at AS "CompletedAt", c.created_by_member_id AS "CreatedByMemberId",
                   c.created_at AS "CreatedAt", c.updated_at AS "UpdatedAt", c.version AS "Version", c.retry_count AS "RetryCount"
              FROM messaging.campaigns c
             WHERE c.tenant_id = {tenantId}
             ORDER BY c.created_at DESC, c.id DESC
             OFFSET {skip} LIMIT {pageSize}
            """).ToListAsync(cancellationToken);
        var total = await dbContext.Database.SqlQuery<int>(
            $"""SELECT count(*)::int AS "Value" FROM messaging.campaigns WHERE tenant_id = {tenantId}""").SingleAsync(cancellationToken);
        return (await WithCountsAsync(campaigns, cancellationToken), total);
    }

    public async Task<CampaignRow?> FindAsync(Guid tenantId, Guid campaignId, CancellationToken cancellationToken)
    {
        var campaigns = await dbContext.Database.SqlQuery<CampaignColumns>(
            $"""
            SELECT c.id AS "Id", c.connection_id AS "ConnectionId", c.name AS "Name", c.template_name AS "TemplateName", c.template_language AS "TemplateLanguage",
                   c.template_category AS "TemplateCategory", c.parameters::text AS "Parameters", c.audience_tag_ids AS "AudienceTagIds",
                   c.audience_customer_ids AS "AudienceCustomerIds", c.status AS "Status", c.failure_reason AS "FailureReason", c.failure_code AS "FailureCode",
                   c.scheduled_at AS "ScheduledAt", c.started_at AS "StartedAt", c.completed_at AS "CompletedAt", c.created_by_member_id AS "CreatedByMemberId",
                   c.created_at AS "CreatedAt", c.updated_at AS "UpdatedAt", c.version AS "Version", c.retry_count AS "RetryCount"
              FROM messaging.campaigns c
             WHERE c.tenant_id = {tenantId} AND c.id = {campaignId}
            """).ToListAsync(cancellationToken);
        return campaigns.Count == 0 ? null : (await WithCountsAsync(campaigns, cancellationToken))[0];
    }

    public async Task<(IReadOnlyList<CampaignRecipientRow> Items, int Total)> ListRecipientsAsync(
        Guid tenantId, Guid campaignId, CampaignRecipientStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var skip = (page - 1) * pageSize;
        var filter = status?.ToString();
        // messageStatus sólo para lo que Meta aceptó: el mensaje de un destinatario Failed o en reintento está Failed por diseño (se
        // inserta así antes de llamar a Meta, ver CampaignStore) y no es una novedad que la pantalla deba pintar.
        var rows = await dbContext.Database.SqlQuery<RecipientColumns>(
            $"""
            SELECT r.customer_id AS "CustomerId", r.customer_name AS "CustomerName", r.status AS "Status", r.failure_code AS "FailureCode",
                   r.failure_reason AS "FailureReason", r.sent_at AS "SentAt", r.conversation_id AS "ConversationId",
                   CASE WHEN r.status = 'Sent' THEN m.status END AS "MessageStatus", r.retry_count AS "RetryCount"
              FROM messaging.campaign_recipients r
              LEFT JOIN messaging.messages m ON m.id = r.message_id
             WHERE r.campaign_id = {campaignId} AND r.tenant_id = {tenantId} AND ({filter}::text IS NULL OR r.status = {filter})
             ORDER BY r.customer_name, r.customer_id
             OFFSET {skip} LIMIT {pageSize}
            """).ToListAsync(cancellationToken);
        var total = await dbContext.Database.SqlQuery<int>(
            $"""
            SELECT count(*)::int AS "Value" FROM messaging.campaign_recipients
             WHERE campaign_id = {campaignId} AND tenant_id = {tenantId} AND ({filter}::text IS NULL OR status = {filter})
            """).SingleAsync(cancellationToken);
        return ([.. rows.Select(row => new CampaignRecipientRow(
            row.CustomerId, row.CustomerName, Enum.Parse<CampaignRecipientStatus>(row.Status), row.FailureCode, row.FailureReason, row.SentAt,
            row.ConversationId, row.MessageStatus is { } code ? MessageColumnCodes.ToStatus(code) : null, row.RetryCount))], total);
    }

    private async Task<IReadOnlyList<CampaignRow>> WithCountsAsync(List<CampaignColumns> campaigns, CancellationToken cancellationToken)
    {
        var ids = campaigns.Select(campaign => campaign.Id).ToArray();
        // Retriable: la misma regla que CampaignFailureCodes.IsRetriableByHand y que el UPDATE de CampaignRepository.SaveRetriedAsync.
        var counts = (await dbContext.Database.SqlQuery<CountColumns>(
            $"""
            SELECT r.campaign_id AS "CampaignId",
                   count(*)::int AS "Total",
                   (count(*) FILTER (WHERE r.status IN ('Pending', 'Sending')))::int AS "Pending",
                   (count(*) FILTER (WHERE r.status = 'Sent'))::int AS "Sent",
                   (count(*) FILTER (WHERE r.status = 'Sent' AND m.status IN ({Delivered}, {Read})))::int AS "Delivered",
                   (count(*) FILTER (WHERE r.status = 'Sent' AND m.status = {Read}))::int AS "Read",
                   (count(*) FILTER (WHERE r.status = 'Sent' AND r.conversation_id IS NOT NULL AND EXISTS (
                        SELECT 1 FROM messaging.messages i
                         WHERE i.conversation_id = r.conversation_id AND i.direction = 1 AND i.occurred_at > r.sent_at)))::int AS "Replied",
                   (count(*) FILTER (WHERE r.status = 'Failed'))::int AS "Failed",
                   (count(*) FILTER (WHERE r.status = 'Skipped'))::int AS "Skipped",
                   (count(*) FILTER (WHERE (r.status = 'Failed' AND r.failure_code IS DISTINCT FROM {CampaignFailureCodes.MetaOptedOut})
                                        OR (r.status = 'Skipped' AND r.failure_code = {CampaignFailureCodes.CampaignFailed})))::int AS "Retriable"
              FROM messaging.campaign_recipients r
              LEFT JOIN messaging.messages m ON m.id = r.message_id
             WHERE r.campaign_id = ANY({ids})
             GROUP BY r.campaign_id
            """).ToListAsync(cancellationToken)).ToDictionary(row => row.CampaignId);
        var zero = new CampaignCountsDto(0, 0, 0, 0, 0, 0, 0, 0, 0);
        return [.. campaigns.Select(campaign => new CampaignRow(
            campaign.Id, campaign.ConnectionId, campaign.Name, campaign.TemplateName, campaign.TemplateLanguage, campaign.TemplateCategory,
            CampaignParametersJson.Parse(campaign.Parameters), campaign.AudienceTagIds, campaign.AudienceCustomerIds,
            Enum.Parse<CampaignStatus>(campaign.Status), campaign.FailureReason, campaign.FailureCode, campaign.ScheduledAt, campaign.StartedAt,
            campaign.CompletedAt, campaign.CreatedByMemberId, campaign.CreatedAt, campaign.UpdatedAt, campaign.Version, campaign.RetryCount,
            counts.TryGetValue(campaign.Id, out var found)
                ? new CampaignCountsDto(
                    found.Total, found.Pending, found.Sent, found.Delivered, found.Read, found.Replied, found.Failed, found.Skipped, found.Retriable)
                : zero))];
    }

    private sealed class CampaignColumns
    {
        public Guid Id { get; set; }

        public Guid ConnectionId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string TemplateName { get; set; } = string.Empty;

        public string TemplateLanguage { get; set; } = string.Empty;

        public string TemplateCategory { get; set; } = string.Empty;

        public string? Parameters { get; set; }

        public Guid[] AudienceTagIds { get; set; } = [];

        public Guid[] AudienceCustomerIds { get; set; } = [];

        public string Status { get; set; } = string.Empty;

        public string? FailureReason { get; set; }

        public string? FailureCode { get; set; }

        public DateTimeOffset? ScheduledAt { get; set; }

        public DateTimeOffset? StartedAt { get; set; }

        public DateTimeOffset? CompletedAt { get; set; }

        public Guid CreatedByMemberId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public long Version { get; set; }

        public int RetryCount { get; set; }
    }

    private sealed class RecipientColumns
    {
        public Guid CustomerId { get; set; }

        public string CustomerName { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? FailureCode { get; set; }

        public string? FailureReason { get; set; }

        public DateTimeOffset? SentAt { get; set; }

        public Guid? ConversationId { get; set; }

        public short? MessageStatus { get; set; }

        public int RetryCount { get; set; }
    }

    private sealed class CountColumns
    {
        public Guid CampaignId { get; set; }

        public int Total { get; set; }

        public int Pending { get; set; }

        public int Sent { get; set; }

        public int Delivered { get; set; }

        public int Read { get; set; }

        public int Replied { get; set; }

        public int Failed { get; set; }

        public int Skipped { get; set; }

        public int Retriable { get; set; }
    }
}
