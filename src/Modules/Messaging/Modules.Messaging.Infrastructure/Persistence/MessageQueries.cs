using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>§7.6: el hilo por <c>IX_messages_thread</c> con keyset <c>(occurred_at, id) &lt; (@t, @id)</c>.
/// La búsqueda del historial se suma en la Task 15.</summary>
internal sealed class MessageQueries(MessagingDbContext dbContext) : IMessageQueries
{
    internal static readonly Expression<Func<MessageRecord, MessageRow>> RowProjection = message => new MessageRow(
        message.Id, message.ConversationId, message.Direction, message.Kind, message.Text, message.Caption, message.Details, message.Status,
        message.FailureCode, message.OccurredAt, message.SentByMemberId, message.ClientId,
        message.Media == null ? null : new MessageMediaRow(message.Media.MimeType, message.Media.FileName), message.ReplyToMessageId);

    public Task<MessageCursor?> FindCursorAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken) =>
        dbContext.Messages.AsNoTracking()
            .Where(message => message.ConversationId == conversationId && message.Id == messageId)
            .Select(message => new MessageCursor(message.OccurredAt, message.Id))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<MessageRow>> ListThreadAsync(Guid conversationId, MessageCursor? before, int take, CancellationToken cancellationToken)
    {
        var query = dbContext.Messages.AsNoTracking().Where(message => message.ConversationId == conversationId);
        if (before is { } cursor)
        {
            // Comparación de fila de Postgres, el mismo orden que el índice (§7.6, «Hilo hacia atrás»).
            query = query.Where(message =>
                EF.Functions.LessThan(ValueTuple.Create(message.OccurredAt, message.Id), ValueTuple.Create(cursor.OccurredAt, cursor.Id)));
        }

        return await query
            .OrderByDescending(message => message.OccurredAt).ThenByDescending(message => message.Id)
            .Take(take)
            .Select(RowProjection)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, ReplyTargetRow>> FindReplyTargetsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, ReplyTargetRow>();
        }

        var wanted = ids.ToArray();
        return await dbContext.Messages.AsNoTracking()
            .Where(message => message.TenantId == tenantId && wanted.Contains(message.Id))
            .Select(message => new ReplyTargetRow(message.Id, message.ConversationId, message.Direction, message.Kind, message.Text, message.Caption, message.Wamid))
            .ToDictionaryAsync(target => target.Id, cancellationToken);
    }
}
