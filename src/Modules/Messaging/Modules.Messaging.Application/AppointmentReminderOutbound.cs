using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Agenda: el recordatorio de una cita por WhatsApp. <c>WaId</c> son sólo dígitos, con indicativo. <c>SentByMemberId</c> es la persona que
/// firma el mensaje (la fila exige un autor: la elige la Agenda). <c>ClientId</c> es la llave de idempotencia que deriva de la cita: un
/// reintento después de un envío que sí salió no manda un segundo recordatorio. <c>CustomerId</c> es el cliente de la cita: la conversación
/// se abre por el teléfono y se cuelga de él. <c>Template</c> es la fila aprobada que devuelve <see cref="IAppointmentReminderReadiness"/>,
/// nunca la constante. <c>Values</c> va por clave (<c>nombre_cliente</c>, <c>servicio</c>, <c>fecha_hora</c>, <c>recurso</c>, <c>sede</c>).
/// </summary>
public sealed record AppointmentReminderSend(
    Guid TenantId, Guid ConnectionId, Guid SentByMemberId, Guid ClientId,
    string WaId,
    Guid? CustomerId,
    string RecipientName,
    ApprovedTemplate Template,
    IReadOnlyDictionary<string, string> Values);

public abstract record AppointmentReminderSendResult
{
    /// <summary>Salió, o ya había salido con ese <c>ClientId</c> (la misma fila).</summary>
    public sealed record Sent(Guid MessageId, Guid ConversationId) : AppointmentReminderSendResult;

    /// <summary><paramref name="Reason"/> es un código de spec 2026-10-24 §3.4 (<c>paused</c>, <c>credentials_rejected</c>, …).</summary>
    public sealed record ConnectionUnavailable(string? Reason) : AppointmentReminderSendResult;

    public sealed record Failed(string Code) : AppointmentReminderSendResult;
}

public interface IAppointmentReminderOutbound
{
    Task<AppointmentReminderSendResult> SendAsync(AppointmentReminderSend send, CancellationToken cancellationToken);
}

/// <summary>Agenda: el recordatorio sale por <see cref="PlainTemplateOutbound"/>, al hilo del número y sin asignar a nadie, y sin orden
/// posicional (la plantilla nació con variables con nombre). A diferencia del aviso de Mayorista sí lleva cliente: la cita es de un cliente
/// de QEP. Si ese cliente ya no existe, el envío no sale y devuelve <c>messaging.customer.not_found</c>.</summary>
public sealed class AppointmentReminderOutbound(
    IConversationQueries conversations,
    IConversationRepository repository,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IClock clock,
    IConversationOpener opener,
    TemplateSenderValues senderValues) : IAppointmentReminderOutbound
{
    private readonly PlainTemplateOutbound _core =
        new(conversations, repository, outbound, meta, connections, notifier, receipts, clock, opener, senderValues);

    public async Task<AppointmentReminderSendResult> SendAsync(AppointmentReminderSend send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        PlainTemplateSendResult result;
        try
        {
            result = await _core.SendAsync(
                new PlainTemplateSend(
                    send.TenantId, send.ConnectionId, send.SentByMemberId, send.ClientId, send.WaId, send.CustomerId, send.RecipientName,
                    send.Template, send.Values, LegacyPositionalKeys: null),
                cancellationToken);
        }
        // Abrir la conversación de un número con el cliente de la cita ya borrado (ConversationOpener): no se envió nada.
        catch (ResourceNotFoundException exception) when (exception.Code == MessagingErrorCodes.CustomerNotFound)
        {
            return new AppointmentReminderSendResult.Failed(MessagingErrorCodes.CustomerNotFound);
        }

        return result switch
        {
            PlainTemplateSendResult.Sent sent => new AppointmentReminderSendResult.Sent(sent.MessageId, sent.ConversationId),
            PlainTemplateSendResult.ConnectionUnavailable unavailable => new AppointmentReminderSendResult.ConnectionUnavailable(unavailable.Reason),
            PlainTemplateSendResult.Failed failed => new AppointmentReminderSendResult.Failed(failed.Code),
            _ => throw new InvalidOperationException($"Unexpected send result {result.GetType().Name}."),
        };
    }
}
