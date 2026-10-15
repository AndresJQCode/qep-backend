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

    /// <summary>Spec 2026-10-14 §3: el tope de un texto en la Messaging API de Instagram. Lo aplica el handler, que es quien
    /// conoce el canal de la conversación; el validador sólo sabe el de WhatsApp.</summary>
    public const int InstagramTextMaxLength = 1000;

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
/// Bloquea una sola fila de mensaje, nunca la conversación. El envío no sube <c>version</c>; la autoasignación sí.
/// Las guardas, el reclamo y la tabla viven en <see cref="OutboundGuards"/> (spec 2026-10-11, D-V3).</summary>
public sealed class SendMessageHandler(
    IConversationQueries conversations,
    IConversationRepository repository,
    IMessageQueries messages,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IInstagramClient instagram,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier,
    IValidator<SendMessageCommand> validator)
    : ICommandHandler<SendMessageCommand, MessageDto>
{
    public const string CallbackPrefix = "qep:";

    /// <summary>§8.3: un 4xx de Graph sin <c>error.code</c> legible se guarda con este <c>failure_code</c>; no está en
    /// la tabla de §10.3, así que <c>MessageFailureReasons</c> lo muestra con el texto genérico.</summary>
    public const int UnknownGraphErrorCode = 0;

    // CS9124: los parámetros que van a la guarda no se usan en los métodos; el reloj y el reclamo se piden a ella.
    private readonly OutboundGuards _guards = new(
        conversations, repository, messages, outbound, connections, memberNames, membershipDirectory, tenantModules, executionContext, clock, notifier);

    public async Task<MessageDto> HandleAsync(SendMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var target = await _guards.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        var conversation = target.Conversation;

        // Spec 2026-10-10 §8.5 paso 2: lo que ya salió, ya salió, antes de mirar estado, ventana o dueño.
        if (await _guards.FindAlreadySentAsync(conversation.Id, command.ClientId!.Value, cancellationToken) is { } sent)
        {
            return await _guards.ToDtoAsync(command.TenantId, OutboundGuards.ExistingRow(conversation.Id, sent, command.ClientId), cancellationToken);
        }

        var text = command.Text!.Trim();
        // Spec 2026-10-14 §5.2: el tope de Instagram es menor; se decide acá, con el canal a la vista, y antes de la autoasignación.
        if (conversation.Channel == ConversationChannel.Instagram && text.Length > SendMessageValidator.InstagramTextMaxLength)
        {
            throw new ValidationException([new ValidationFailure("text", "Escribe un mensaje de hasta 1000 caracteres: es el máximo de Instagram.")]);
        }

        var permit = await _guards.AdmitAsync(command.TenantId, target, command.ReplyTo, cancellationToken);
        var draft = new OutboundDraft(Guid.CreateVersion7(), conversation.Id, command.TenantId, conversation.ConnectionId, command.ClientId!.Value, text, target.Member, permit.Now,
            permit.Quoted?.Id, permit.Quoted?.Wamid);
        await using var claim = await _guards.ClaimAsync(draft, cancellationToken);

        // Desde acá, todo cierre del reclamo va con CancellationToken.None: si la persona cierra la pestaña
        // (RequestAborted) mientras Meta ya aceptó el mensaje, un rollback por cancelación borraría la fila y el
        // reintento con el mismo clientId lo mandaría dos veces. La llamada a Meta igual tiene tope: los 10 s
        // del cliente messaging.meta-graph.
        if (await OutboundGuards.TakeInFlightAsync(claim) is { } inFlight)
        {
            return await _guards.ToDtoAsync(command.TenantId, OutboundGuards.ExistingRow(conversation.Id, inFlight, command.ClientId), cancellationToken);
        }

        SendResult result;
        if (conversation.Channel == ConversationChannel.Instagram)
        {
            // Spec 2026-10-14 §6.2: a Instagram no viaja la cita (la API no la tiene); el replyTo queda guardado igual.
            var igSender = await _guards.ResolveInstagramSenderAsync(command.TenantId, conversation.ConnectionId, claim, cancellationToken);
            result = await instagram.SendTextAsync(igSender, conversation.UserId!, text, CancellationToken.None);
        }
        else
        {
            var sender = await _guards.ResolveSenderAsync(command.TenantId, conversation.ConnectionId, claim, cancellationToken);
            result = await meta.SendTextAsync(
                sender, SendTarget.For(conversation.UserId, conversation.WaId), text, CallbackPrefix + claim.MessageId.ToString("D"), permit.Quoted?.Wamid, CancellationToken.None);
        }
        // §8.3: occurred_at del saliente = hora del servidor al recibir la respuesta de Meta.
        var answeredAt = _guards.UtcNow;
        await _guards.SettleAsync(command.TenantId, conversation.ConnectionId, claim, result, answeredAt, metaMediaId: null, afterRollback: null);
        return await _guards.ToDtoAsync(
            command.TenantId,
            new MessageRow(claim.MessageId, conversation.Id, MessageDirection.Outbound, MessageKind.Text, text, null, null, MessageStatus.Sent, null, answeredAt, target.Member,
                command.ClientId, null, ReplyToMessageId: permit.Quoted?.Id),
            CancellationToken.None);
    }
}
