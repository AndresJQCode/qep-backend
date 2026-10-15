using Microsoft.AspNetCore.SignalR;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api.Realtime;

/// <summary>
/// Spec 2026-10-14, contrato §2.1: lo que el listener de <c>NOTIFY</c> entrega a este pod se traduce en los tres
/// eventos del navegador. <c>conversationChanged</c> e <c>inboundMessage</c> van al grupo del tenant;
/// <c>notify</c> —el que suena— va al grupo del miembro asignado, o al tenant entero si nadie tiene la conversación
/// (decisión 4). El payload son ids: el cliente invalida e relee por HTTP, donde sigue la autorización.
/// </summary>
internal sealed class MessagingHubEventSink(IHubContext<MessagingHub> hub) : IMessagingEventSink
{
    public const string ConversationChanged = "conversationChanged";

    public const string InboundMessage = "inboundMessage";

    public const string Notify = "notify";

    public async Task DispatchAsync(MessagingEvent notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var tenant = hub.Clients.Group(MessagingHub.TenantGroup(notification.TenantId));
        switch (notification.Kind)
        {
            case MessagingEventKinds.ConversationChanged:
                await tenant.SendAsync(ConversationChanged, new { conversationId = notification.ConversationId }, cancellationToken);
                break;

            case MessagingEventKinds.InboundMessage:
                await tenant.SendAsync(InboundMessage, new { conversationId = notification.ConversationId, messageId = notification.MessageId }, cancellationToken);
                var target = notification.AssignedMemberId is { } member
                    ? hub.Clients.Group(MessagingHub.MemberGroup(notification.TenantId, member))
                    : tenant;
                await target.SendAsync(Notify, new { conversationId = notification.ConversationId, reason = "inbound" }, cancellationToken);
                break;

            default:
                // Un kind que este build no conoce (otro pod más nuevo) se ignora: el polling lo cubre.
                break;
        }
    }
}
