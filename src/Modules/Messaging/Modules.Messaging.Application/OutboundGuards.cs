using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-10 §8.5, pasos 2 a 4: la conversación del tenant y la membresía activa de quien envía.</summary>
internal sealed record OutboundTarget(ConversationRow Conversation, Guid Member);

/// <summary>Lo que dejan pasar los pasos 6 a 9: la hora del servidor al admitir y el citado ya validado (o <c>null</c>).</summary>
internal sealed record OutboundPermit(DateTimeOffset Now, ReplyTargetRow? Quoted);

/// <summary>
/// Spec 2026-10-11 §6.3 (D-V3): las guardas que comparten el envío de texto y el de nota de voz, en el orden del spec
/// 2026-10-10 §8.5, y la tabla de respuestas de Meta del spec 2026-10-09 §8.3. Cada handler pone lo suyo entre el
/// reclamo y la tabla: el texto llama a Meta una vez; la nota de voz sube a R2, sube a Meta y envía.
/// No se registra en DI: cada handler la arma con sus propias dependencias, y por eso las pruebas de
/// <see cref="SendMessageHandler"/> no cambiaron al extraerla.
/// </summary>
internal sealed class OutboundGuards(
    IConversationQueries conversations,
    IConversationRepository repository,
    IMessageQueries messages,
    IOutboundMessages outbound,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier,
    ReadReceipts receipts)
{
    public DateTimeOffset UtcNow => clock.UtcNow;

    /// <summary>Pasos 2 a 4: tenant, permiso y módulo → conversación del tenant → membresía activa.</summary>
    public async Task<OutboundTarget> LoadAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, tenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var conversation = await conversations.FindAsync(tenantId, conversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(conversationId);
        var member = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken)
            ?? throw new RequestForbiddenException("authorization.denied", "The subject does not have an active membership in this tenant.");
        return new OutboundTarget(conversation, member);
    }

    /// <summary>Paso 5: lo que ya salió, ya salió, antes de mirar estado, ventana o dueño (sin candado). Un
    /// <c>Failed</c> no cuenta: se reenvía.</summary>
    public async Task<ExistingOutbound?> FindAlreadySentAsync(Guid conversationId, Guid clientId, CancellationToken cancellationToken) =>
        await outbound.FindByClientIdAsync(conversationId, clientId, cancellationToken) is { Status: not MessageStatus.Failed } sent ? sent : null;

    /// <summary>Pasos 6 a 9: status Open → ventana → el citado → dueño. Todo lo que puede fallar por validación va antes
    /// de la autoasignación: un request inválido no cambia estado.</summary>
    public Task<OutboundPermit> AdmitAsync(Guid tenantId, OutboundTarget target, Guid? replyTo, CancellationToken cancellationToken) =>
        AdmitAsync(tenantId, target, replyTo, requireOpenWindow: true, cancellationToken);

    /// <summary>Spec 2026-10-11 §5.2: lo mismo que <see cref="AdmitAsync(Guid, OutboundTarget, Guid?, CancellationToken)"/> sin la
    /// ventana de 24 h ni la cita: una plantilla existe justamente para escribir con la ventana cerrada.</summary>
    public Task<OutboundPermit> AdmitTemplateAsync(Guid tenantId, OutboundTarget target, CancellationToken cancellationToken) =>
        AdmitAsync(tenantId, target, replyTo: null, requireOpenWindow: false, cancellationToken);

    /// <summary>Tenant, permiso, módulo y membresía activa, sin conversación todavía (<c>POST /conversations</c>): el id de la
    /// membresía de quien llama.</summary>
    public async Task<Guid> AuthorizeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, tenantId, MessagingPermissions.ConversationManage, cancellationToken);
        return await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken)
            ?? throw new RequestForbiddenException("authorization.denied", "The subject does not have an active membership in this tenant.");
    }

    private async Task<OutboundPermit> AdmitAsync(
        Guid tenantId, OutboundTarget target, Guid? replyTo, bool requireOpenWindow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var conversation = target.Conversation;
        var now = clock.UtcNow;
        if (conversation.Status != ConversationStatus.Open)
        {
            throw new MessagingDomainException(MessagingErrorCodes.ConversationNotOpen, "The conversation is resolved; reopen it to answer.");
        }

        if (requireOpenWindow && (conversation.LastInboundAt is not { } lastInbound || lastInbound.Add(Conversation.WindowLength) <= now))
        {
            throw new MessagingDomainException(MessagingErrorCodes.WindowClosed, "More than 24 hours passed since the person last wrote.");
        }

        // §8.5: lo citado es de esta conversación, salió por WhatsApp y no es una reacción ni un evento. Va antes del
        // dueño: un request que no pasa la validación no cambia estado, así que una cita inválida no autoasigna.
        // Nunca se manda a Meta un context.message_id de otra conversación.
        ReplyTargetRow? quoted = null;
        if (replyTo is { } quotedId)
        {
            var targets = await messages.FindReplyTargetsAsync(tenantId, [quotedId], cancellationToken);
            if (!targets.TryGetValue(quotedId, out quoted) || quoted.ConversationId != conversation.Id || quoted.Wamid is null
                || quoted.Kind is MessageKind.Reaction or MessageKind.Event)
            {
                throw new ValidationException([new ValidationFailure("replyTo", "Elige un mensaje de esta conversación que se pueda citar.")]);
            }
        }

        // §8.5: sólo el asignado responde; sin asignar, responder es tomar (atómico, fuera del candado largo).
        // Si Meta rechaza después, la conversación queda asignada: intentar responder es tomar.
        if (conversation.AssignedMemberId is { } owner
                ? owner != target.Member
                : await repository.TryAutoAssignAsync(tenantId, conversation.Id, target.Member, executionContext.SubjectId, now, cancellationToken) == AutoAssignOutcome.AssignedToOther)
        {
            throw new MessagingDomainException(MessagingErrorCodes.AssignedToOther, "The conversation is assigned to someone else.");
        }

        return new OutboundPermit(now, quoted);
    }

    public Task<IOutboundClaim> ClaimAsync(OutboundDraft draft, CancellationToken cancellationToken) => outbound.ClaimAsync(draft, cancellationToken);

    /// <summary>Idempotencia entre dos requests en vuelo con el mismo clientId: el segundo esperó el candado del primero.
    /// Devuelve lo existente (ya con el reclamo cerrado) o <c>null</c> si hay que seguir.</summary>
    public static async Task<ExistingOutbound?> TakeInFlightAsync(IOutboundClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!claim.Inserted && claim.Existing is { } existing && existing.Status != MessageStatus.Failed)
        {
            await claim.RollbackAsync(CancellationToken.None);
            return existing;
        }

        return null;
    }

    /// <summary>Paso 11: la conexión <c>Active</c>, ya con el reclamo tomado.</summary>
    public async Task<MessagingSender> ResolveSenderAsync(Guid tenantId, Guid connectionId, IOutboundClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        try
        {
            return await ResolveSenderAsync(tenantId, connectionId, cancellationToken);
        }
        catch (MessagingDomainException)
        {
            await claim.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>La misma conexión <c>Active</c>, sin reclamo: el adjunto del asesor sube a Meta antes de reclamar la fila, así que
    /// no hay nada que deshacer si la conexión no sirve.</summary>
    public async Task<MessagingSender> ResolveSenderAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        await connections.ResolveSenderAsync(tenantId, connectionId, cancellationToken)
            ?? throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "The WhatsApp connection is paused, needs attention or was deleted.");

    /// <summary>Spec 2026-10-14 §6.2: la conexión de Instagram <c>Active</c>, ya con el reclamo tomado. Mismo contrato que
    /// <see cref="ResolveSenderAsync(Guid, Guid, IOutboundClaim, CancellationToken)"/>.</summary>
    public async Task<InstagramSender> ResolveInstagramSenderAsync(Guid tenantId, Guid connectionId, IOutboundClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        try
        {
            return await ResolveInstagramSenderAsync(tenantId, connectionId, cancellationToken);
        }
        catch (MessagingDomainException)
        {
            await claim.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<InstagramSender> ResolveInstagramSenderAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        await connections.ResolveInstagramSenderAsync(tenantId, connectionId, cancellationToken)
            ?? throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "The Instagram connection is paused, needs attention or was deleted.");

    /// <summary>
    /// §8.3, la tabla de respuestas. Vuelve sólo si quedó <c>Sent</c>; en cualquier otro caso cierra el reclamo y lanza.
    /// Todo cierre va con <c>CancellationToken.None</c>: si Meta ya aceptó, un rollback por cancelación borraría la fila
    /// y el reintento con el mismo clientId lo mandaría dos veces. <paramref name="afterRollback"/> corre después de un
    /// rollback por credencial (la nota de voz borra ahí lo que subió a R2). <paramref name="seen"/> es el <c>unread_count</c> que
    /// tenía la conversación al cargarla en este request: lo que el asesor tenía a la vista al responder (spec 2026-10-16 §3.1).
    /// </summary>
    public async Task SettleAsync(
        Guid tenantId, Guid connectionId, IOutboundClaim claim, SendResult result, DateTimeOffset answeredAt, string? metaMediaId, Func<Task>? afterRollback,
        int seen)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Outcome)
        {
            case SendOutcome.Sent when !string.IsNullOrEmpty(result.Wamid):
                await claim.CommitSentAsync(result.Wamid, answeredAt, metaMediaId, CancellationToken.None);
                // Spec 2026-10-16 §3.1: responder quita la marca. Va antes del aviso para que ese único aviso ya lleve el contador nuevo.
                var replied = await MarkRepliedAsync(tenantId, claim.ConversationId, seen);
                await NotifyConsolidatedAsync(tenantId, claim.ConversationId);
                // Sólo con el contador en 0: si entró algo durante el envío, el acuse de WhatsApp sobre el último wamid lo marcaría
                // leído también, y en Instagram mark_seen es por persona. Se manda cuando alguien quite la marca después.
                if (replied is { RemainingUnread: 0 })
                {
                    await receipts.SendAsync(tenantId, claim.ConversationId, replied.Receipt, clock.UtcNow, CancellationToken.None);
                }

                return;

            case SendOutcome.GraphError when result.Code is MetaErrorCodes.TokenExpired or MetaErrorCodes.NumberUnregistered:
                // §8.3: la credencial o el número ya no sirven; la fila no se guarda y la conexión pasa a NeedsAttention.
                await claim.RollbackAsync(CancellationToken.None);
                if (afterRollback is not null)
                {
                    await afterRollback();
                }

                await ReportRejectedAsync(tenantId, connectionId, MetaErrorCodes.RejectedReason(result.Code.Value));
                throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "Meta rejected the connection credentials.");

            case SendOutcome.GraphError when result.Code is 131047:
                await claim.CommitFailedAsync(131047, result.Title, answeredAt, metaMediaId, CancellationToken.None);
                await NotifyConsolidatedAsync(tenantId, claim.ConversationId);
                throw new MessagingDomainException(MessagingErrorCodes.WindowClosed, "Meta closed the 24-hour window.");

            case SendOutcome.GraphError when result.Code is MetaErrorCodes.InstagramOutsideWindow:
                // Spec 2026-10-14 §6.2: en Instagram «fuera de la ventana» viaja con el código genérico de permiso (10) y el
                // subcódigo 2534022; el cliente ya lo distinguió por subcódigo. La fila queda Failed con el 10, que la tabla traduce.
                await claim.CommitFailedAsync(result.Code.Value, result.Title, answeredAt, metaMediaId, CancellationToken.None);
                throw new MessagingDomainException(MessagingErrorCodes.WindowClosed, "Instagram closed the 24-hour window.");

            case SendOutcome.GraphError:
                await claim.CommitFailedAsync(result.Code ?? SendMessageHandler.UnknownGraphErrorCode, result.Title, answeredAt, metaMediaId, CancellationToken.None);
                await NotifyConsolidatedAsync(tenantId, claim.ConversationId);
                throw new MessagingDomainException(MessagingErrorCodes.MessageRejected, "Meta rejected the message.");

            default:
                // Timeout, 5xx, red o un 2xx sin messages[0].id: Meta pudo haberlo aceptado (riesgo residual de §8.3).
                await claim.CommitFailedAsync(MessageFailureReasons.Unconfirmed, null, answeredAt, metaMediaId, CancellationToken.None);
                await NotifyConsolidatedAsync(tenantId, claim.ConversationId);
                throw new MessagingDomainException(MessagingErrorCodes.MessageRejected, "The send could not be confirmed with Meta.");
        }
    }

    /// <summary>Spec 2026-10-14: Sent o Failed quedaron commiteados, así que el otro asesor del hilo tiene algo que leer. Un
    /// rollback no avisa. Un fallo del aviso no deshace el envío ni cambia la respuesta: Meta ya aceptó, y el polling cubre.</summary>
    private async Task NotifyConsolidatedAsync(Guid tenantId, Guid conversationId)
    {
        try
        {
            await notifier.ConversationChangedAsync(tenantId, conversationId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Application no tiene logger; la causa no se pierde del todo: el listener del otro lado registra la suya.
            _ = exception;
        }
    }

    /// <summary>Spec 2026-10-16 §3.1: resta lo que el asesor vio, no lo pone en 0, para que un entrante que llegó durante el envío
    /// sobreviva. Un fallo se traga como el aviso: Meta ya aceptó y la respuesta del envío no cambia; la marca queda y se quita a
    /// mano.</summary>
    private async Task<RepliedTarget?> MarkRepliedAsync(Guid tenantId, Guid conversationId, int seen)
    {
        try
        {
            return await repository.MarkRepliedAsync(tenantId, conversationId, seen, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Como en NotifyConsolidatedAsync, aquí no hay logger. Sin resta tampoco sale acuse: a Meta nunca le llega un leído que la
            // base no refleja.
            _ = exception;
            return null;
        }
    }

    // El mensaje sale con el mapeo de la lista (MessageMapping): mismo sentBy.displayName, nunca null (D-M20).
    // En la idempotencia se devuelve lo guardado, no lo del request.
    public async Task<MessageDto> ToDtoAsync(Guid tenantId, MessageRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        var names = row.SentByMemberId is { } member
            ? await memberNames.FindAsync(tenantId, [member], cancellationToken)
            : new Dictionary<Guid, string>();
        // §5.1: repetir un clientId devuelve el replyTo guardado, aunque el cuerpo traiga otro.
        var targets = row.ReplyToMessageId is { } quoted
            ? await messages.FindReplyTargetsAsync(tenantId, [quoted], cancellationToken)
            : new Dictionary<Guid, ReplyTargetRow>();
        return MessageMapping.ToDto(row, tenantId, names, targets);
    }

    /// <summary>Spec 2026-10-11 §6.5: una nota de voz guardada vuelve como audio, sin texto y con su medio.</summary>
    public static MessageRow ExistingRow(Guid conversationId, ExistingOutbound existing, Guid? clientId)
    {
        ArgumentNullException.ThrowIfNull(existing);
        // Spec 2026-10-11 §5.2: una plantilla guardada vuelve con su texto ya armado y su detalle (de ahí sale template).
        var isTemplate = existing.Kind == MessageKind.Template;
        return new MessageRow(
            existing.Id, conversationId, MessageDirection.Outbound, existing.Kind, existing.Kind is MessageKind.Text or MessageKind.Template ? existing.Text : null, existing.Caption,
            isTemplate ? existing.DetailsJson : null,
            existing.Status, null, existing.OccurredAt, existing.SentByMemberId, clientId,
            existing.MediaMimeType is { } mimeType ? new MessageMediaRow(mimeType, existing.MediaFileName) : null, existing.ReplyToMessageId);
    }

    // Si Integrations no logra pasar la conexión a NeedsAttention, la respuesta sigue siendo el 422 documentado;
    // la causa viaja como InnerException y ApiExceptionHandler la deja en platform.request_failures (Application no
    // tiene logger). El próximo envío vuelve a recibir 190/133010 y reintenta el reporte.
    private async Task ReportRejectedAsync(Guid tenantId, Guid connectionId, string failureCode)
    {
        try
        {
            await connections.ReportRejectedAsync(tenantId, connectionId, failureCode, CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.ConnectionUnavailable, "Meta rejected the connection credentials; the connection state could not be updated.", exception);
        }
    }
}
