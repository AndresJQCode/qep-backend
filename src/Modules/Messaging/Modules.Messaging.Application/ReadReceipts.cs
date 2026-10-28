using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-16 §3.1: el acuse de leído a Meta, compartido por marcar leída, responder y resolver. Best effort:
/// nunca lanza (salvo cancelación) porque la conversación ya quedó leída en la base y un fallo de Meta no debe deshacerlo.</summary>
public sealed partial class ReadReceipts(
    IMessagingConnectionDirectory connections,
    IWhatsAppCloudClient meta,
    IInstagramClient instagram,
    ILogger<ReadReceipts> logger)
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Read receipt for conversation {ConversationId} could not be sent to Meta; the conversation is marked read anyway.")]
    private static partial void LogReceiptFailed(ILogger logger, Guid conversationId, Exception? exception);

    public async Task SendAsync(Guid tenantId, Guid conversationId, ReadReceiptTarget target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (target.LastInboundWamid is not { } wamid || !Conversation.CanAcknowledgeReading(wamid, target.LastInboundAt, now))
        {
            return;
        }

        try
        {
            if (target.Channel == ConversationChannel.Instagram)
            {
                // Spec 2026-10-14 decisión 14 (V-5): sender_action mark_seen, por persona y no por mensaje; best effort.
                var igSender = await connections.ResolveInstagramSenderAsync(tenantId, target.ConnectionId, cancellationToken);
                if (igSender is not null && target.UserId is { } userId && !await instagram.MarkSeenAsync(igSender, userId, cancellationToken))
                {
                    LogReceiptFailed(logger, conversationId, null);
                }

                return;
            }

            var sender = await connections.ResolveSenderAsync(tenantId, target.ConnectionId, cancellationToken);
            if (sender is not null && !await meta.MarkReadAsync(sender, wamid, cancellationToken))
            {
                LogReceiptFailed(logger, conversationId, null);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogReceiptFailed(logger, conversationId, exception);
        }
    }
}
