using System.Globalization;
using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Lo que comparten el seguimiento del hilo y las acciones del asesor sobre los mensajes de un ticket.</summary>
internal static class SupportThreadRules
{
    private static readonly string[] StatusFields = ["status"];

    /// <summary>
    /// Plan E6: un entrante vinculado (solo o desde «sin clasificar») reabre un ticket que esperaba al cliente; uno resuelto no (decisión
    /// pendiente P4). Lo reabre el sistema: el evento no tiene actor y la auditoría sale como sistema, porque el cambio lo causa el
    /// mensaje del cliente y no quien lo clasificó.
    /// <para>Spec 2026-11-22 §6: si reabrió y el ticket tiene responsable, éste recibe «el cliente respondió» en su campana, salvo que él
    /// mismo haya causado el cambio (<paramref name="actorMemberId"/>: el asesor que clasifica a mano un entrante; null en el seguimiento
    /// del hilo, que es el sistema). Sin responsable no hay aviso inmediato: lo cubre el aviso de «sin asignar».</para>
    /// </summary>
    public static void ReopenIfWaiting(
        SupportTicket ticket,
        SupportReadContext context,
        ISupportAuditPublisher auditPublisher,
        ISupportIntegrationEvents events,
        Guid? actorMemberId)
    {
        if (ticket.Status is not SupportTicketStatus.WaitingOnCustomer)
        {
            return;
        }

        ticket.ChangeStatus(SupportTicketStatus.Open, null, context.Now, context.Clock);
        auditPublisher.PublishSystem(
            ticket.TenantId,
            SupportAuditActions.TicketStatusChanged,
            SupportAuditActions.Resources.Ticket,
            ticket.Id.ToString(),
            StatusFields,
            context.Now);

        if (ticket.AssigneeMemberId is { } assignee && actorMemberId != assignee)
        {
            events.PublishCustomerReplied(ticket, context.Now);
        }
    }

    /// <summary>Los mensajes de la conversación con esos ids, como los dibuja Soporte, por id. Un id que ya no está en Mensajería no
    /// aparece. Los autores miembros se nombran en un solo lote.</summary>
    public static async Task<IReadOnlyDictionary<Guid, SupportThreadMessageDto>> ReadMessagesAsync(
        ISupportConversationDirectory conversations,
        ISupportMemberDirectory members,
        Guid tenantId,
        Guid conversationId,
        IReadOnlyCollection<Guid> messageIds,
        CancellationToken cancellationToken)
    {
        if (messageIds.Count == 0)
        {
            return new Dictionary<Guid, SupportThreadMessageDto>();
        }

        var messages = await conversations.FindMessagesAsync(tenantId, conversationId, messageIds, cancellationToken);
        var memberIds = messages.Select(message => message.SentByMemberId).OfType<Guid>().Distinct().ToArray();
        var names = memberIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await members.NamesAsync(tenantId, memberIds, cancellationToken);

        return messages.ToDictionary(
            message => message.Id,
            message => new SupportThreadMessageDto(
                message.Id,
                message.Direction,
                message.Kind,
                message.Text,
                message.Caption,
                message.OccurredAt,
                AuthorName(message, names),
                message.MediaMimeType is not null,
                message.MediaMimeType,
                message.MediaFileName));
    }

    public static SupportTicketRefDto ToRef(SupportTicket ticket) => new(ticket.Id.Value, ticket.DisplayNumber, ticket.Subject, ticket.Version);

    private static string? AuthorName(SupportThreadMessage message, IReadOnlyDictionary<Guid, string> names) =>
        message.SentByMemberId is { } memberId
            ? names.TryGetValue(memberId, out var name) ? name : SupportTicketDtoComposer.UnknownName
            : message.SentByApiKeyName;
}

/// <summary>El cursor opaco del expediente: la hora del último mensaje (ticks UTC) y su id, separados por <c>_</c>. Sólo dígitos,
/// guiones bajos y hexadecimal: viaja en la query sin escapar.</summary>
public static class SupportMessageCursor
{
    public static string Encode(DateTimeOffset occurredAt, Guid messageId) =>
        $"{occurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}_{messageId:N}";

    public static bool TryDecode(string? text, out (DateTimeOffset OccurredAt, Guid MessageId) cursor)
    {
        cursor = default;
        var parts = text?.Split('_');
        if (parts is not [var ticks, var id]
            || !long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var utcTicks)
            || utcTicks < DateTimeOffset.MinValue.UtcTicks
            || utcTicks > DateTimeOffset.MaxValue.UtcTicks
            || !Guid.TryParseExact(id, "N", out var messageId))
        {
            return false;
        }

        cursor = (new DateTimeOffset(utcTicks, TimeSpan.Zero), messageId);
        return true;
    }
}
