using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

public sealed record BlockContactCommand(Guid TenantId, Guid ConversationId) : ICommand<ConversationSummary>;

public sealed record UnblockContactCommand(Guid TenantId, Guid ConversationId) : ICommand<ConversationSummary>;

/// <summary>Spec 2026-10-26 §3.2: bloquear el número del contacto en Meta. Orden: tenant, permiso y módulo → conversación (404) →
/// membresía → <b>estado propio</b> (ya bloqueada: 200 sin Meta ni evento, aunque la ventana esté cerrada) → canal y ventana (422) →
/// conexión <c>Active</c> (422) → Meta → evento y auditoría. Sin If-Match: el resultado no depende de la versión que vio el cliente,
/// y un conflicto después de que Meta aceptó se reintenta (<see cref="ConversationAssignmentSteps.CommitWithRetryAsync"/>).</summary>
public sealed class BlockContactHandler(
    ConversationAssignmentSteps steps, IMessagingConnectionDirectory connections, IWhatsAppCloudClient client, ILogger<BlockContactHandler> logger)
    : ICommandHandler<BlockContactCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(BlockContactCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (conversation, member) = await steps.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        var now = steps.Now;
        // §3.3: el estado propio va primero, así no dependemos de cómo responde Meta a «ya estaba bloqueado» (no se publica).
        if (conversation.IsBlocked)
        {
            return await steps.CommitAsync(command.TenantId, conversation, [], now, cancellationToken);
        }

        conversation.EnsureCanBlock(now);
        var sender = await ContactBlockSteps.ResolveSenderAsync(connections, command.TenantId, conversation.ConnectionId, cancellationToken);
        // El número se fija acá: el reintento relee la conversación y su wa_id pudo cambiar, pero Meta bloqueó éste.
        var waId = conversation.WaId!;
        var result = await client.BlockUserAsync(sender, waId, cancellationToken);
        await ContactBlockSteps.EnsureAppliedAsync(
            ContactBlockResponse.Classify(result, ContactBlockDirection.Block), ContactBlockDirection.Block, connections, logger, command.TenantId, conversation);
        // Meta ya bloqueó: guardarlo no depende de que el navegador siga esperando.
        return await steps.CommitWithRetryAsync(command.TenantId, conversation, loaded => loaded.Block(member, waId, now), now, CancellationToken.None);
    }
}

/// <summary>Spec 2026-10-26 §3.2: desbloquear. Mismo orden que bloquear, sin canal ni ventana (Meta desbloquea fuera de las 24 horas)
/// y con el número que se bloqueó (<see cref="Conversation.BlockedWaId"/>), no el actual: un cambio de número no deja el viejo bloqueado.
/// Una que no está bloqueada responde 200 sin Meta ni evento.</summary>
public sealed class UnblockContactHandler(
    ConversationAssignmentSteps steps, IMessagingConnectionDirectory connections, IWhatsAppCloudClient client, ILogger<UnblockContactHandler> logger)
    : ICommandHandler<UnblockContactCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(UnblockContactCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (conversation, member) = await steps.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        var now = steps.Now;
        if (!conversation.IsBlocked)
        {
            return await steps.CommitAsync(command.TenantId, conversation, [], now, cancellationToken);
        }

        var sender = await ContactBlockSteps.ResolveSenderAsync(connections, command.TenantId, conversation.ConnectionId, cancellationToken);
        var result = await client.UnblockUserAsync(sender, conversation.BlockedWaId!, cancellationToken);
        await ContactBlockSteps.EnsureAppliedAsync(
            ContactBlockResponse.Classify(result, ContactBlockDirection.Unblock), ContactBlockDirection.Unblock, connections, logger, command.TenantId, conversation);
        return await steps.CommitWithRetryAsync(command.TenantId, conversation, loaded => loaded.Unblock(member, now), now, CancellationToken.None);
    }
}

/// <summary>Lo que comparten bloquear y desbloquear: la conexión y la traducción del veredicto de Meta a la respuesta (§3.3). El log
/// lleva la conversación y lo que Meta contestó, nunca el número ni el token: <c>conversationId</c> alcanza para encontrarlo.</summary>
internal static partial class ContactBlockSteps
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Meta did not {Direction} the contact of conversation {ConversationId} in tenant {TenantId} (code {Code}, subcode {Subcode}, fbtrace {FbTraceId})")]
    public static partial void LogContactBlockFailed(
        ILogger logger, ContactBlockDirection direction, Guid tenantId, Guid conversationId, int? code, int? subcode, string? fbTraceId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Meta rejected unblocking the contact of conversation {ConversationId} in tenant {TenantId}; it stays blocked (fbtrace {FbTraceId})")]
    public static partial void LogContactUnblockRejected(ILogger logger, Guid tenantId, Guid conversationId, string? fbTraceId);

    public static async Task<MessagingSender> ResolveSenderAsync(
        IMessagingConnectionDirectory connections, Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        await connections.ResolveSenderAsync(tenantId, connectionId, cancellationToken)
            ?? throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "The WhatsApp connection is paused, needs attention or was deleted.");

    /// <summary>Vuelve sólo si Meta lo aplicó; cualquier otro veredicto termina el request sin tocar la conversación.</summary>
    public static async Task EnsureAppliedAsync(
        ContactBlockAnswer answer, ContactBlockDirection direction, IMessagingConnectionDirectory connections, ILogger logger, Guid tenantId, Conversation conversation)
    {
        switch (answer.Verdict)
        {
            case ContactBlockVerdict.Applied:
                return;

            case ContactBlockVerdict.WindowClosed:
                throw new MessagingDomainException(MessagingErrorCodes.BlockWindowClosed, "Meta closed the customer window.");

            case ContactBlockVerdict.UnblockRejected:
                LogContactUnblockRejected(logger, tenantId, conversation.Id, answer.FbTraceId);
                throw new MessagingDomainException(MessagingErrorCodes.UnblockRejected, "Meta rejected unblocking the contact.");

            case ContactBlockVerdict.ConnectionRejected:
                // §8.3, como un envío: la conexión pasa a NeedsAttention y la respuesta es la de una conexión que no sirve.
                await ConnectionRejection.ReportAsync(connections, tenantId, conversation.ConnectionId, answer.Code!.Value);
                throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "Meta rejected the connection credentials.");

            default:
                LogContactBlockFailed(logger, direction, tenantId, conversation.Id, answer.Code, answer.Subcode, answer.FbTraceId);
                throw new UpstreamServiceException(MessagingErrorCodes.BlockFailed, "Meta did not confirm the change to the contact block.");
        }
    }
}
