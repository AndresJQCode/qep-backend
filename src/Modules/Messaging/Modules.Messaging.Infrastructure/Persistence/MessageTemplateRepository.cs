using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-14 §4.3. El orden es ordinal en memoria y no la collation de la base (como las respuestas rápidas): un WABA
/// tiene decenas de plantillas, no miles.</summary>
internal sealed class MessageTemplateRepository(MessagingDbContext dbContext) : IMessageTemplateRepository
{
    public Task<MessageTemplate?> FindAsync(Guid tenantId, Guid connectionId, Guid templateId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.SingleOrDefaultAsync(
            template => template.TenantId == tenantId && template.ConnectionId == connectionId && template.Id == templateId, cancellationToken);

    public Task<MessageTemplate?> FindByMetaIdAsync(Guid tenantId, Guid connectionId, string metaTemplateId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.SingleOrDefaultAsync(
            template => template.TenantId == tenantId && template.ConnectionId == connectionId && template.MetaTemplateId == metaTemplateId, cancellationToken);

    public Task<MessageTemplate?> FindByNameAsync(Guid tenantId, Guid connectionId, string name, string language, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.SingleOrDefaultAsync(
            template => template.TenantId == tenantId && template.ConnectionId == connectionId && template.Name == name && template.Language == language,
            cancellationToken);

    public Task<MessageTemplate?> FindQuotationAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.FirstOrDefaultAsync(
            template => template.TenantId == tenantId && template.ConnectionId == connectionId && template.Kind == MessageTemplateKind.Quotation,
            cancellationToken);

    public async Task<IReadOnlyList<MessageTemplate>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.MessageTemplates
            .Where(template => template.TenantId == tenantId && template.ConnectionId == connectionId)
            .ToListAsync(cancellationToken);
        return Ordered(rows);
    }

    public async Task<IReadOnlyList<MessageTemplate>> ListApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.MessageTemplates.AsNoTracking()
            .Where(template => template.TenantId == tenantId && template.ConnectionId == connectionId && template.Status == MessageTemplateStatuses.Approved)
            .ToListAsync(cancellationToken);
        return Ordered(rows);
    }

    public void Add(MessageTemplate messageTemplate) => dbContext.MessageTemplates.Add(messageTemplate);

    public void Remove(MessageTemplate messageTemplate) => dbContext.MessageTemplates.Remove(messageTemplate);

    public Task<bool> IsReferencedByActiveCampaignAsync(Guid tenantId, Guid connectionId, string name, string language, CancellationToken cancellationToken) =>
        dbContext.Campaigns.AnyAsync(
            campaign => campaign.TenantId == tenantId && campaign.ConnectionId == connectionId
                && campaign.TemplateName == name && campaign.TemplateLanguage == language
                && (campaign.Status == CampaignStatus.Scheduled || campaign.Status == CampaignStatus.Sending),
            cancellationToken);

    public async Task<DateTimeOffset?> LastSyncAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var record = await dbContext.TemplateMirrorSyncs.AsNoTracking()
            .SingleOrDefaultAsync(sync => sync.TenantId == tenantId && sync.ConnectionId == connectionId, cancellationToken);
        return record?.SyncedAt;
    }

    public async Task MarkSyncedAsync(Guid tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var record = await dbContext.TemplateMirrorSyncs
            .SingleOrDefaultAsync(sync => sync.TenantId == tenantId && sync.ConnectionId == connectionId, cancellationToken);
        if (record is null)
        {
            dbContext.TemplateMirrorSyncs.Add(new TemplateMirrorSyncRecord { TenantId = tenantId, ConnectionId = connectionId, SyncedAt = now });
        }
        else
        {
            record.SyncedAt = now;
        }
    }

    private static MessageTemplate[] Ordered(List<MessageTemplate> rows) =>
        rows.OrderBy(template => template.Name, StringComparer.Ordinal).ThenBy(template => template.Language, StringComparer.Ordinal).ToArray();
}
