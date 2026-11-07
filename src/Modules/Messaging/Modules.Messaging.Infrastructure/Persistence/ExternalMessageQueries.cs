using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-11-01 parte 2. Siempre por <c>tenant_id</c> y <c>sent_by_kind = 4</c>: el integrador sólo ve lo que envió una clave.</summary>
internal sealed class ExternalMessageQueries(MessagingDbContext dbContext) : IExternalMessageQueries
{
    public Task<ExternalMessageRow?> FindAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
        FromApiKeys(tenantId)
            .Where(message => message.Id == messageId)
            .Select(message => new ExternalMessageRow(message.Id, message.ConversationId, message.ConnectionId, message.Status, message.FailureCode))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ExternalMessageRow>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        var ids = messageIds.ToArray();
        return await FromApiKeys(tenantId)
            .Where(message => ids.Contains(message.Id))
            .Select(message => new ExternalMessageRow(message.Id, message.ConversationId, message.ConnectionId, message.Status, message.FailureCode))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListFailedIdsAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken) =>
        await FromApiKeys(tenantId)
            .Where(message => message.Status == MessageStatus.Failed && message.OccurredAt >= since)
            .OrderBy(message => message.OccurredAt).ThenBy(message => message.Id)
            .Select(message => message.Id)
            .ToListAsync(cancellationToken);

    private IQueryable<MessageRecord> FromApiKeys(Guid tenantId) =>
        dbContext.Messages.AsNoTracking()
            .Where(message => message.TenantId == tenantId && message.SentByKind == MessageAuthorKind.ApiKey);
}
