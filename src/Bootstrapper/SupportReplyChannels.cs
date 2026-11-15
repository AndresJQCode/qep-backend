using BuildingBlocks.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Support.Application;

namespace Bootstrapper;

/// <summary>
/// Soporte fase 2 (plan E12 a E14): traduce <see cref="ISupportReplyOutbound"/> y la disponibilidad de la plantilla «Aviso de respuesta» de
/// Mensajería a los tipos de Soporte, que no ve Mensajería. Las conexiones salen del mismo origen que usa el envío de cotizaciones por
/// WhatsApp (<see cref="IMessagingConnectionDirectory.ListActiveAsync"/>: activas, por nombre), filtradas a las de WhatsApp con la
/// plantilla aprobada (ruling R2). Los motivos viajan por nombre: <see cref="SupportChannelReasons"/> copia <see cref="SupportReplyReasons"/>.
/// </summary>
internal sealed class SupportReplyChannels(
    ISupportReplyOutbound outbound,
    IMessagingConnectionDirectory connections,
    ISupportReplyTemplateReadiness readiness) : ISupportReplyChannels
{
    public async Task<SupportThreadStatus?> FindThreadAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        await outbound.FindThreadAsync(tenantId, conversationId, cancellationToken) is { } thread
            ? new SupportThreadStatus(thread.ConversationId, thread.Channel, thread.Blocked, thread.WindowClosesAt, thread.ConnectionId)
            : null;

    public async Task<IReadOnlyList<SupportConnectionOption>> ListTemplateConnectionsAsync(
        Guid tenantId, CancellationToken cancellationToken)
    {
        var options = new List<SupportConnectionOption>();
        foreach (var connection in await connections.ListActiveAsync(tenantId, cancellationToken))
        {
            if (connection.Channel == nameof(ConversationChannel.WhatsApp)
                && await readiness.FindApprovedAsync(tenantId, connection.Id, cancellationToken) is not null)
            {
                options.Add(new SupportConnectionOption(connection.Id, connection.Name));
            }
        }

        return options;
    }

    public async Task<bool> IsTemplateApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        await readiness.FindApprovedAsync(tenantId, connectionId, cancellationToken) is not null;

    public async Task<SupportChannelSendResult> SendThreadAsync(SupportChannelThreadSend send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        return Translate(await outbound.SendThreadAsync(
            new SupportThreadReplySend(send.TenantId, send.ConversationId, send.ClientId, send.AuthorMemberId, send.Text, send.UploadId),
            cancellationToken));
    }

    public async Task<SupportChannelSendResult> SendTemplateAsync(SupportChannelTemplateSend send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        try
        {
            return Translate(await outbound.SendTemplateAsync(
                new SupportTemplateReplySend(
                    send.TenantId, send.ConversationId, send.ConnectionId, send.WaId, send.CustomerId, send.RecipientName, send.ClientId,
                    send.AuthorMemberId, send.TicketNumber, send.Text),
                cancellationToken));
        }
        // Abrir la conversación de un número con el cliente del ticket ya borrado (ConversationOpener): no se envió nada.
        catch (ResourceNotFoundException exception) when (exception.Code == MessagingErrorCodes.CustomerNotFound)
        {
            return new SupportChannelSendResult.Rejected(SupportChannelReasons.CustomerNotFound);
        }
    }

    private static SupportChannelSendResult Translate(SupportReplySendResult result) => result switch
    {
        SupportReplySendResult.Sent sent => new SupportChannelSendResult.Sent(sent.MessageId, sent.ConversationId),
        SupportReplySendResult.Rejected rejected => new SupportChannelSendResult.Rejected(rejected.Reason),
        SupportReplySendResult.Failed failed => new SupportChannelSendResult.Failed(failed.MessageId, failed.Code),
        _ => throw new InvalidOperationException($"Unknown support reply result '{result.GetType().Name}'."),
    };
}
