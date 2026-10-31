using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record SendTypingIndicatorCommand(Guid TenantId, Guid ConversationId) : ICommand<bool>;

/// <summary>Mientras un asesor escribe, el contacto ve «escribiendo». Sólo sale si el asesor podría contestar ya mismo; si no, es un no-op
/// con 204. Es best effort como <see cref="ReadReceipts"/>: nunca lanza salvo cancelación. No persiste nada ni toma la conversación
/// (version, unread_count, auditoría y eventos no cambian): el frontend lo llama cada 20 s por asesor, y escribir no es responder.
/// Devuelve <c>true</c> si Meta lo aceptó.</summary>
public sealed partial class SendTypingIndicatorHandler(
    IConversationQueries conversations,
    IMessagingConnectionDirectory connections,
    IWhatsAppCloudClient meta,
    IInstagramClient instagram,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    ILogger<SendTypingIndicatorHandler> logger)
    : ICommandHandler<SendTypingIndicatorCommand, bool>
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Typing indicator for conversation {ConversationId} could not be sent to Meta.")]
    private static partial void LogTypingFailed(ILogger logger, Guid conversationId, Exception? exception);

    public async Task<bool> HandleAsync(SendTypingIndicatorCommand command, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var conversation = await conversations.FindAsync(command.TenantId, command.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(command.ConversationId);
        if (!CouldBeAnswered(conversation, clock.UtcNow))
        {
            return false;
        }

        // Va al final para ahorrarle la consulta a los no-ops: el 403 sólo importa cuando de verdad saldría algo hacia Meta.
        var member = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw new RequestForbiddenException("authorization.denied", "The subject does not have an active membership in this tenant.");
        if (conversation.AssignedMemberId is { } owner && owner != member)
        {
            return false;
        }

        return await SendAsync(conversation, cancellationToken);
    }

    /// <summary>Las guardas de <see cref="OutboundGuards"/> sin tomar la conversación, más el asistente: mientras atiende, el compositor del
    /// asesor está bloqueado. <see cref="Conversation.CanAcknowledgeReading"/> exige el wamid, que WhatsApp necesita para el indicador.</summary>
    private static bool CouldBeAnswered(ConversationRow conversation, DateTimeOffset now) =>
        conversation.Status == ConversationStatus.Open
        && conversation.BlockedAt is null
        && conversation.AutomationStatus != ConversationAutomationStatus.Active
        && conversation.LastInboundAt is { } lastInbound && lastInbound.Add(Conversation.WindowLength) > now
        && Conversation.CanAcknowledgeReading(conversation.LastInboundWamid, conversation.LastInboundAt, now);

    private async Task<bool> SendAsync(ConversationRow conversation, CancellationToken cancellationToken)
    {
        try
        {
            bool? accepted = conversation.Channel == ConversationChannel.Instagram
                ? await SendToInstagramAsync(conversation, cancellationToken)
                : await SendToWhatsAppAsync(conversation, cancellationToken);
            if (accepted == false)
            {
                LogTypingFailed(logger, conversation.Id, null);
            }

            return accepted == true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogTypingFailed(logger, conversation.Id, exception);
            return false;
        }
    }

    private async Task<bool?> SendToWhatsAppAsync(ConversationRow conversation, CancellationToken cancellationToken) =>
        await connections.ResolveSenderAsync(conversation.TenantId, conversation.ConnectionId, cancellationToken) is { } sender
            ? await meta.SendTypingIndicatorAsync(sender, conversation.LastInboundWamid!, cancellationToken)
            : null;

    private async Task<bool?> SendToInstagramAsync(ConversationRow conversation, CancellationToken cancellationToken) =>
        conversation.UserId is { } recipient
        && await connections.ResolveInstagramSenderAsync(conversation.TenantId, conversation.ConnectionId, cancellationToken) is { } sender
            ? await instagram.SendTypingOnAsync(sender, recipient, cancellationToken)
            : null;
}
