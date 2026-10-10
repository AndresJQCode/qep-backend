using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record SendMessageCommand(Guid TenantId, Guid ConversationId, Guid? ClientId, string? Text, Guid? ReplyTo = null) : ICommand<MessageDto>;

/// <summary>§6.4: <c>text</c> no vacío tras <c>Trim</c>, ≤ 4096, sin <c>\0</c> ni otros caracteres de control
/// (salto de línea, retorno y tabulador sí: un mensaje de WhatsApp los lleva); <c>clientId</c> GUID no vacío;
/// <c>replyTo</c>, si viene, GUID no vacío (spec 2026-10-10 §5.1). Las claves de <c>errors</c> son las del cuerpo JSON
/// (<c>OverridePropertyName</c>).</summary>
public sealed class SendMessageValidator : AbstractValidator<SendMessageCommand>
{
    public const int TextMaxLength = 4096;

    public SendMessageValidator()
    {
        RuleFor(command => command.Text)
            .Must(text => text is not null && text.Trim().Length is > 0 and <= TextMaxLength && !text.Any(IsForbiddenControl))
            .OverridePropertyName("text").WithMessage("Escribe un mensaje de hasta 4096 caracteres.");
        RuleFor(command => command.ClientId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("clientId");
        RuleFor(command => command.ReplyTo).NotEqual(Guid.Empty).When(command => command.ReplyTo is not null).OverridePropertyName("replyTo");
    }

    private static bool IsForbiddenControl(char character) => char.IsControl(character) && character is not ('\n' or '\r' or '\t');
}

/// <summary>Spec 2026-10-10 §8.5, en este orden: validador → tenant, permiso y módulo → conversación del tenant →
/// membresía → lo ya enviado con ese <c>clientId</c> (sin candado) → status Open → ventana → el citado → dueño (sólo
/// el asignado responde; sin asignar, responder es tomar, con un UPDATE condicional en su propia transacción) →
/// reclamo → conexión Active → Meta → tabla de respuestas. Todo lo que puede fallar por validación va antes de la
/// autoasignación: un request inválido no cambia estado.
/// La transacción del reclamo sigue abierta mientras Meta responde (≤ 10 s, §8.3): es lo que hace esperar
/// a un segundo request con el mismo <c>clientId</c> y lo que impide que se vea una fila a medio enviar.
/// Bloquea una sola fila de mensaje, nunca la conversación. El envío no sube <c>version</c>; la autoasignación sí.</summary>
public sealed class SendMessageHandler(
    IConversationQueries conversations,
    IConversationRepository repository,
    IMessageQueries messages,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<SendMessageCommand> validator)
    : ICommandHandler<SendMessageCommand, MessageDto>
{
    public const string CallbackPrefix = "qep:";

    /// <summary>§8.3: un 4xx de Graph sin <c>error.code</c> legible se guarda con este <c>failure_code</c>; no está en
    /// la tabla de §10.3, así que <c>MessageFailureReasons</c> lo muestra con el texto genérico.</summary>
    public const int UnknownGraphErrorCode = 0;

    public async Task<MessageDto> HandleAsync(SendMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var conversation = await conversations.FindAsync(command.TenantId, command.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(command.ConversationId);
        var member = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw new RequestForbiddenException("authorization.denied", "The subject does not have an active membership in this tenant.");

        // Spec 2026-10-10 §8.5 paso 2: lo que ya salió, ya salió, antes de mirar estado, ventana o dueño.
        if (await outbound.FindByClientIdAsync(conversation.Id, command.ClientId!.Value, cancellationToken) is { Status: not MessageStatus.Failed } sent)
        {
            return await ToDtoAsync(command, ExistingRow(conversation.Id, sent, command.ClientId), cancellationToken);
        }

        var now = clock.UtcNow;
        if (conversation.Status != ConversationStatus.Open)
        {
            throw new MessagingDomainException(MessagingErrorCodes.ConversationNotOpen, "The conversation is resolved; reopen it to answer.");
        }

        if (conversation.LastInboundAt is not { } lastInbound || lastInbound.Add(Conversation.WindowLength) <= now)
        {
            throw new MessagingDomainException(MessagingErrorCodes.WindowClosed, "More than 24 hours passed since the person last wrote.");
        }

        // §8.5: lo citado es de esta conversación, salió por WhatsApp y no es una reacción ni un evento. Va antes del
        // dueño: un request que no pasa la validación no cambia estado, así que una cita inválida no autoasigna.
        // Nunca se manda a Meta un context.message_id de otra conversación.
        ReplyTargetRow? quoted = null;
        if (command.ReplyTo is { } replyTo)
        {
            var targets = await messages.FindReplyTargetsAsync(command.TenantId, [replyTo], cancellationToken);
            if (!targets.TryGetValue(replyTo, out quoted) || quoted.ConversationId != conversation.Id || quoted.Wamid is null
                || quoted.Kind is MessageKind.Reaction or MessageKind.Event)
            {
                throw new ValidationException([new ValidationFailure("replyTo", "Elige un mensaje de esta conversación que se pueda citar.")]);
            }
        }

        // §8.5: sólo el asignado responde; sin asignar, responder es tomar (atómico, fuera del candado largo).
        // Si Meta rechaza después, la conversación queda asignada: intentar responder es tomar.
        if (conversation.AssignedMemberId is { } owner
                ? owner != member
                : await repository.TryAutoAssignAsync(command.TenantId, conversation.Id, member, executionContext.SubjectId, now, cancellationToken) == AutoAssignOutcome.AssignedToOther)
        {
            throw new MessagingDomainException(MessagingErrorCodes.AssignedToOther, "The conversation is assigned to someone else.");
        }

        var text = command.Text!.Trim();
        var draft = new OutboundDraft(Guid.CreateVersion7(), conversation.Id, command.TenantId, conversation.ConnectionId, command.ClientId!.Value, text, member, now,
            quoted?.Id, quoted?.Wamid);
        await using var claim = await outbound.ClaimAsync(draft, cancellationToken);

        // Desde acá, todo cierre del reclamo va con CancellationToken.None: si la persona cierra la pestaña
        // (RequestAborted) mientras Meta ya aceptó el mensaje, un rollback por cancelación borraría la fila y el
        // reintento con el mismo clientId lo mandaría dos veces. La llamada a Meta igual tiene tope: los 10 s
        // del cliente messaging.meta-graph.

        // Idempotencia entre dos requests en vuelo con el mismo clientId: el segundo esperó el candado del primero.
        if (!claim.Inserted && claim.Existing is { } existing && existing.Status != MessageStatus.Failed)
        {
            await claim.RollbackAsync(CancellationToken.None);
            return await ToDtoAsync(command, ExistingRow(conversation.Id, existing, command.ClientId), cancellationToken);
        }

        var sender = await connections.ResolveSenderAsync(command.TenantId, conversation.ConnectionId, cancellationToken);
        if (sender is null)
        {
            await claim.RollbackAsync(CancellationToken.None);
            throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "The WhatsApp connection is paused, needs attention or was deleted.");
        }

        var result = await meta.SendTextAsync(
            sender, SendTarget.For(conversation.UserId, conversation.WaId), text, CallbackPrefix + claim.MessageId.ToString("D"), quoted?.Wamid, CancellationToken.None);
        // §8.3: occurred_at del saliente = hora del servidor al recibir la respuesta de Meta.
        var answeredAt = clock.UtcNow;
        switch (result.Outcome)
        {
            case SendOutcome.Sent when !string.IsNullOrEmpty(result.Wamid):
                await claim.CommitSentAsync(result.Wamid, answeredAt, CancellationToken.None);
                return await ToDtoAsync(
                    command,
                    new MessageRow(claim.MessageId, conversation.Id, MessageDirection.Outbound, MessageKind.Text, text, null, null, MessageStatus.Sent, null, answeredAt, member,
                        command.ClientId, null, ReplyToMessageId: quoted?.Id),
                    CancellationToken.None);

            case SendOutcome.GraphError when result.Code is 190 or 133010:
                // §8.3: la credencial o el número ya no sirven; la fila no se guarda y la conexión pasa a NeedsAttention.
                await claim.RollbackAsync(CancellationToken.None);
                await ReportRejectedAsync(command.TenantId, conversation.ConnectionId, result.Code == 190 ? "token_expired" : "number_unregistered");
                throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "Meta rejected the connection credentials.");

            case SendOutcome.GraphError when result.Code is 131047:
                await claim.CommitFailedAsync(131047, result.Title, answeredAt, CancellationToken.None);
                throw new MessagingDomainException(MessagingErrorCodes.WindowClosed, "Meta closed the 24-hour window.");

            case SendOutcome.GraphError:
                await claim.CommitFailedAsync(result.Code ?? UnknownGraphErrorCode, result.Title, answeredAt, CancellationToken.None);
                throw new MessagingDomainException(MessagingErrorCodes.MessageRejected, "Meta rejected the message.");

            default:
                // Timeout, 5xx, red o un 2xx sin messages[0].id: Meta pudo haberlo aceptado (riesgo residual de §8.3).
                await claim.CommitFailedAsync(MessageFailureReasons.Unconfirmed, null, answeredAt, CancellationToken.None);
                throw new MessagingDomainException(MessagingErrorCodes.MessageRejected, "The send could not be confirmed with Meta.");
        }
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

    // El mensaje sale con el mapeo de la lista (MessageMapping): mismo sentBy.displayName, nunca null (D-M20).
    // En la idempotencia se devuelve el texto guardado, no el del request.
    private async Task<MessageDto> ToDtoAsync(SendMessageCommand command, MessageRow row, CancellationToken cancellationToken)
    {
        var names = row.SentByMemberId is { } member
            ? await memberNames.FindAsync(command.TenantId, [member], cancellationToken)
            : new Dictionary<Guid, string>();
        // §5.1: repetir un clientId devuelve el replyTo guardado, aunque el cuerpo traiga otro.
        var targets = row.ReplyToMessageId is { } quoted
            ? await messages.FindReplyTargetsAsync(command.TenantId, [quoted], cancellationToken)
            : new Dictionary<Guid, ReplyTargetRow>();
        return MessageMapping.ToDto(row, command.TenantId, names, targets);
    }

    private static MessageRow ExistingRow(Guid conversationId, ExistingOutbound existing, Guid? clientId) =>
        new(existing.Id, conversationId, MessageDirection.Outbound, MessageKind.Text, existing.Text, null, null, existing.Status, null,
            existing.OccurredAt, existing.SentByMemberId, clientId, null, existing.ReplyToMessageId);
}
