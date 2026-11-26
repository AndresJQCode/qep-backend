using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Modules.Support.Application;

namespace Bootstrapper;

/// <summary>
/// Soporte fase 2 (spec §8.1): las conversaciones y los mensajes que Soporte muestra y vincula, leídos del <c>DbContext</c> de Mensajería
/// sin tracking, como <see cref="PipelineConversationDirectory"/>. La conversación sale de su <c>DbSet</c> público; los mensajes, por SQL
/// crudo porque su <c>DbSet</c> es interno de Mensajería.
///
/// **Lo que lee por nombre.** <c>messaging.messages</c> (<c>id</c>, <c>tenant_id</c>, <c>conversation_id</c>, <c>direction</c>,
/// <c>kind</c>, <c>text</c>, <c>caption</c>, <c>occurred_at</c>, <c>sent_by_member_id</c>, <c>sent_by_chatbot_id</c>,
/// <c>sent_by_api_key_name</c>) y <c>messaging.message_media</c> (<c>message_id</c>, <c>mime_type</c>, <c>file_name</c>). Renombrar una
/// compila y deja el expediente del ticket en 500; lo amarra <c>SupportBootstrapperAdaptersTests</c>. Los códigos de dirección y tipo son
/// los de <see cref="MessageDirection"/> y <see cref="MessageKind"/>, que viajan como <c>smallint</c>.
/// </summary>
internal sealed class SupportConversationDirectory(MessagingDbContext messaging) : ISupportConversationDirectory
{
    private const short EventDirection = (short)MessageDirection.System;

    public Task<SupportConversationInfo?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        messaging.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId && conversation.Id == conversationId)
            .Select(conversation => new SupportConversationInfo(
                conversation.Id,
                conversation.Channel == ConversationChannel.Instagram ? "Instagram" : "WhatsApp",
                conversation.CustomerId,
                conversation.ProfileName,
                conversation.Username,
                conversation.WaId,
                conversation.BlockedAt != null,
                conversation.LastInboundAt))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SupportThreadMessage>> FindMessagesAsync(
        Guid tenantId, Guid conversationId, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Count == 0)
        {
            return [];
        }

        var ids = messageIds.Distinct().ToArray();
        var rows = await messaging.Database.SqlQuery<MessageRow>($"""
            SELECT m.id AS "Id", m.conversation_id AS "ConversationId", m.direction AS "Direction", m.kind AS "Kind", m.text AS "Text",
                   m.caption AS "Caption", m.occurred_at AS "OccurredAt", m.sent_by_member_id AS "SentByMemberId",
                   m.sent_by_chatbot_id AS "SentByChatbotId", m.sent_by_api_key_name AS "SentByApiKeyName",
                   media.mime_type AS "MediaMimeType", media.file_name AS "MediaFileName"
            FROM messaging.messages m
            LEFT JOIN messaging.message_media media ON media.message_id = m.id
            WHERE m.tenant_id = {tenantId} AND m.conversation_id = {conversationId} AND m.id = ANY({ids}) AND m.direction <> {EventDirection}
            """).ToListAsync(cancellationToken);

        return rows
            .Select(row => new SupportThreadMessage(
                row.Id,
                row.ConversationId,
                ((MessageDirection)row.Direction).ToString(),
                ((MessageKind)row.Kind).ToString(),
                row.Text,
                row.Caption,
                row.OccurredAt,
                row.SentByMemberId,
                row.SentByChatbotId,
                row.SentByApiKeyName,
                row.MediaMimeType,
                row.MediaFileName))
            .ToList();
    }

    private sealed class MessageRow
    {
        public Guid Id { get; init; }

        public Guid ConversationId { get; init; }

        public short Direction { get; init; }

        public short Kind { get; init; }

        public string? Text { get; init; }

        public string? Caption { get; init; }

        public DateTimeOffset OccurredAt { get; init; }

        public Guid? SentByMemberId { get; init; }

        public Guid? SentByChatbotId { get; init; }

        public string? SentByApiKeyName { get; init; }

        public string? MediaMimeType { get; init; }

        public string? MediaFileName { get; init; }
    }
}
