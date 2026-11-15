using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

/// <summary>El cuerpo de <c>POST /tickets/{id}/replies</c>. <c>ClientId</c> lo genera la pantalla: el reintento de la misma respuesta
/// lo repite y recibe la misma fila sin volver a enviar. <c>ConnectionId</c> sólo con <c>WhatsAppTemplate</c> (la conexión del selector
/// de <c>reply-options</c>) y <c>UploadId</c> sólo por el hilo, con <c>FileName</c>: el nombre del archivo que la pantalla mandó a la
/// sesión de subida de Mensajería (de su extensión sale la clave del staging). <c>Resolve</c> es «Enviar y marcar Resuelto».</summary>
public sealed record SupportReplyRequest(
    Guid? ClientId, string? Channel, string? Text, Guid? ConnectionId, Guid? UploadId, bool Resolve, string? FileName = null);

/// <param name="ExpectedVersion">La versión del ticket que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record SendTicketReplyCommand(
    Guid TenantId,
    Guid TicketId,
    Guid? ClientId,
    string? Channel,
    string? Text,
    Guid? ConnectionId,
    Guid? UploadId,
    bool Resolve,
    long ExpectedVersion,
    string? FileName = null)
    : ICommand<SupportReplyResultDto>
{
    public static SendTicketReplyCommand From(Guid tenantId, Guid ticketId, SupportReplyRequest request, long expectedVersion) => new(
        tenantId, ticketId, request.ClientId, request.Channel, request.Text, request.ConnectionId, request.UploadId, request.Resolve,
        expectedVersion, request.FileName);
}

/// <summary>
/// Ruling A-c4: cubre cada guarda de <see cref="SupportTicketReply.Start"/> (el <c>clientId</c> no vacío y la regla del texto de
/// <c>SendMessageValidator</c>) y las combinaciones del cuerpo, así ningún cuerpo llega al dominio a lanzar. Lo que depende del ticket
/// (el tope de Instagram, las reglas de la plantilla) lo juzga el handler, antes de crear la fila.
/// </summary>
public sealed class SendTicketReplyValidator : AbstractValidator<SendTicketReplyCommand>
{
    public SendTicketReplyValidator()
    {
        RuleFor(command => command.ClientId).NotNull().NotEqual(Guid.Empty);
        RuleFor(command => command.Channel)
            .Must(text => SupportTicketRules.TryParseExact<SupportReplyChannel>(text, out _))
            .WithMessage("Channel must be Thread, WhatsAppTemplate or Email.");
        RuleFor(command => command.Text)
            .Must(SupportTicketReply.IsValidText)
            .WithMessage($"Escribe una respuesta de 1 a {SupportTicketReply.TextMaxLength} caracteres, sin caracteres de control.");
        RuleFor(command => command.ConnectionId)
            .Null()
            .When(command => command.Channel != nameof(SupportReplyChannel.WhatsAppTemplate))
            .WithMessage("ConnectionId only goes with the WhatsAppTemplate channel.");
        RuleFor(command => command.ConnectionId).NotEqual(Guid.Empty).When(command => command.ConnectionId is not null);
        RuleFor(command => command.UploadId)
            .Null()
            .When(command => command.Channel != nameof(SupportReplyChannel.Thread))
            .WithMessage("UploadId only goes with the Thread channel.");
        RuleFor(command => command.UploadId).NotEqual(Guid.Empty).When(command => command.UploadId is not null);

        // Tarea 11: el adjunto necesita su nombre, y el texto de la respuesta viaja como su pie, que Mensajería corta en 1024.
        RuleFor(command => command.FileName)
            .Must(name => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= SupportReplyAttachmentRules.FileNameMaxLength)
            .When(command => command.UploadId is not null)
            .WithMessage($"Escribe el nombre del archivo, de hasta {SupportReplyAttachmentRules.FileNameMaxLength} caracteres.");
        RuleFor(command => command.FileName)
            .Null()
            .When(command => command.UploadId is null)
            .WithMessage("FileName only goes with an UploadId.");
        RuleFor(command => command.Text)
            .Must(text => text is null || text.Trim().Length <= SupportReplyAttachmentRules.CaptionMaxLength)
            .When(command => command.UploadId is not null)
            .WithMessage($"Con un adjunto, la respuesta puede tener hasta {SupportReplyAttachmentRules.CaptionMaxLength} caracteres.");
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Responder desde el ticket (spec §8.3, plan E13 a E17). Orden: autorización → membresía → validador → ticket (404) → <c>clientId</c>
/// ya visto (terminada o <c>Pending</c> de menos de un minuto: devuelve esa fila sin enviar y sin mirar el <c>If-Match</c>; <c>Pending</c>
/// más vieja: se retoma) → versión (412, sólo una respuesta nueva) → disponibilidad del canal (422 con su código, sin fila) → fila
/// <c>Pending</c> confirmada → envío fuera de transacción → transacción: <c>MarkSent</c>/<c>MarkFailed</c>, el mensaje fijado en el
/// expediente, <c>ReplySent</c>, la conversación nueva de una plantilla (<c>ConversationLinked</c> + <c>TicketOpened</c>), Resuelto si se
/// pidió y salió, auditoría → confirmar → 201.
///
/// El <c>id</c> de la fila es el <c>clientId</c> que viaja a Mensajería, que es idempotente por él: un reintento con una fila todavía
/// <c>Pending</c> (se cayó entre el envío y la marca) vuelve a llamar y Mensajería devuelve el mensaje que ya salió. El correo no tiene esa
/// garantía. Una respuesta que el canal rechaza antes de enviar (la ventana se cerró entre la revisión y el envío) borra su fila y
/// responde 422: el hilo no la muestra y la pantalla tampoco. Una que Meta o el correo rechazan queda <c>Failed</c> y responde 201, igual
/// que el hilo.
/// </summary>
public sealed class SendTicketReplyHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantDirectory tenantDirectory,
    IMembershipDirectory membershipDirectory,
    ISupportTicketRepository tickets,
    ISupportReplyRepository replies,
    ISupportTicketMessageRepository links,
    ISupportCustomerLookup customers,
    ISupportConversationDirectory conversations,
    ISupportReplyChannels channels,
    ISupportReplyEmail email,
    ISupportConversationEvents conversationEvents,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    SupportTicketDtoComposer composer,
    IValidator<SendTicketReplyCommand> validator)
    : ICommandHandler<SendTicketReplyCommand, SupportReplyResultDto>
{
    private static readonly string[] ReplyFields = ["replies"];

    private static readonly string[] StatusFields = ["status"];

    // BM9b: el evento del hilo de la conversación que abrió la plantilla va un microsegundo después del mensaje, para que el hilo los
    // ordene siempre igual.
    private static readonly TimeSpan EventOffset = TimeSpan.FromTicks(10);

    /// <summary>Ruling del controlador: una fila <c>Pending</c> más nueva que esto es de un envío en curso (otro request con el mismo
    /// <c>clientId</c>) y se devuelve sin volver a mandar; una más vieja se cayó entre el envío y la marca, y se retoma.</summary>
    internal static readonly TimeSpan PendingInFlight = TimeSpan.FromSeconds(60);

    public async Task<SupportReplyResultDto> HandleAsync(SendTicketReplyCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.Manage, cancellationToken);
        var actor = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw SupportAuthorization.Denied();
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        if (!SupportTicketRules.TryParseExact<SupportReplyChannel>(command.Channel, out var channel))
        {
            throw new InvalidOperationException($"Channel '{command.Channel}' passed validation but is not a reply channel.");
        }

        var ticket = await tickets.FindAsync(command.TenantId, new SupportTicketId(command.TicketId), cancellationToken)
            ?? throw SupportNotFound.Ticket(command.TicketId);

        // Ruling del controlador (fix round 1): el clientId se mira antes que la versión. El reintento de una respuesta cuya respuesta
        // HTTP se perdió trae el If-Match de antes, ya viejo porque la respuesta subió la versión: tiene que recibir su fila, no un 412.
        var clientId = command.ClientId!.Value;
        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var existing = await replies.FindByClientIdAsync(command.TenantId, ticket.Id, clientId, cancellationToken);
        if (existing is { Status: not SupportReplyStatus.Pending }
            || (existing is { Status: SupportReplyStatus.Pending } && context.Now - existing.CreatedAt < PendingInFlight))
        {
            // Terminada, se devuelve tal cual; Pending y reciente, la está mandando otro request: se devuelve sin volver a mandar.
            return await ResultAsync(ticket, existing.Id, cancellationToken);
        }

        // Una fila Pending vieja se retoma sin mirar la versión: el envío ya se decidió y Mensajería devuelve lo que haya salido con ese
        // clientId. Sólo una respuesta nueva exige la versión que la pantalla cargó.
        if (existing is null && ticket.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The ticket changed after it was loaded.");
        }

        var text = existing?.Text ?? command.Text!.Trim();
        var route = await RouteAsync(ticket, channel, command, text, context.Now, cancellationToken);

        var reply = existing;
        if (reply is null)
        {
            reply = SupportTicketReply.Start(
                command.TenantId, ticket.Id, clientId, channel, route.Delivery, text, route.ConnectionId, actor, context.Now);
            replies.Add(reply);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            // Dos envíos del mismo clientId a la vez: el segundo devuelve la fila del primero, sin enviar.
            catch (ResourceConflictException conflict) when (conflict.Code == SupportErrorCodes.ReplyDuplicateClientId)
            {
                unitOfWork.DiscardChanges();
                var winner = await replies.FindByClientIdAsync(command.TenantId, ticket.Id, clientId, cancellationToken)
                    ?? throw new InvalidOperationException("The reply that won the client id race was not found.");
                var current = await tickets.FindAsync(command.TenantId, ticket.Id, cancellationToken) ?? ticket;
                return await ResultAsync(current, winner.Id, cancellationToken);
            }
        }

        var outcome = await SendAsync(ticket, route, reply, actor, command.UploadId, command.FileName?.Trim(), cancellationToken);
        if (outcome is SupportChannelSendResult.Rejected rejected)
        {
            replies.Remove(reply);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw new SupportDomainException(SupportReplyRules.CodeFor(rejected.Reason), $"The reply was rejected: {rejected.Reason}.");
        }

        var (saved, linkedConversation) = await ApplyOutcomeAsync(ticket, reply, outcome, actor, command.Resolve, cancellationToken);
        if (linkedConversation is { } conversationId)
        {
            await conversationEvents.NotifyChangedAsync(command.TenantId, conversationId, cancellationToken);
        }

        return await ResultAsync(saved, reply.Id, cancellationToken);
    }

    /// <summary>Plan E13 a E15: por dónde sale, o el 422 (403 sin Mensajería) que explica por qué no puede, antes de crear la fila.</summary>
    private async Task<SupportReplyRoute> RouteAsync(
        SupportTicket ticket, SupportReplyChannel channel, SendTicketReplyCommand command, string text, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        SupportReplyRoute route;
        if (channel is SupportReplyChannel.Email)
        {
            route = string.IsNullOrWhiteSpace(ticket.Requester?.Email)
                ? throw Unavailable(channel)
                : new SupportReplyRoute(SupportReplyDelivery.Email, null, null, null);
        }
        else
        {
            await TenantModuleGuard.EnsureEnabledAsync(tenantModules, ticket.TenantId, TenantModuleKeys.Messaging, cancellationToken);
            route = channel is SupportReplyChannel.WhatsAppTemplate && ticket.ConversationId is null
                ? await TemplateToPhoneAsync(ticket, command.ConnectionId, cancellationToken)
                : await ThreadRouteAsync(ticket, now, cancellationToken);
        }

        if (route.Delivery is SupportReplyDelivery.Template)
        {
            if (command.UploadId is not null)
            {
                throw new SupportDomainException(
                    SupportErrorCodes.ReplyAttachmentUnavailable, "An attachment only goes inside the 24-hour window.");
            }

            if (!SupportReplyTextRules.FitsTemplate(text))
            {
                throw new SupportDomainException(
                    SupportErrorCodes.ReplyTemplateTextInvalid,
                    "A template reply cannot have line breaks, tabs or more than four spaces in a row, nor exceed 1024 characters.");
            }
        }

        if (route.Delivery is SupportReplyDelivery.Text
            && ticket.ConversationChannel == SupportConversationRef.Instagram
            && text.Length > SupportReplyTextRules.InstagramTextMaxLength)
        {
            throw new ValidationException(
                [new ValidationFailure(nameof(SendTicketReplyCommand.Text), "Escribe una respuesta de hasta 1000 caracteres: es el máximo de Instagram.")]);
        }

        return route;
    }

    private async Task<SupportReplyRoute> ThreadRouteAsync(SupportTicket ticket, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var availability = await SupportReplyRules.ThreadAsync(channels, ticket, now, cancellationToken);
        return availability.Route
            ?? throw new SupportDomainException(availability.ErrorCode!, $"The thread cannot take a reply: {availability.Reason}.");
    }

    // Plan E14: sin conversación, la plantilla va al celular de la foto (sin «+») por la conexión que eligió el asesor.
    private async Task<SupportReplyRoute> TemplateToPhoneAsync(SupportTicket ticket, Guid? connectionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ticket.Requester?.MobilePhone))
        {
            throw Unavailable(SupportReplyChannel.WhatsAppTemplate);
        }

        if (connectionId is not { } connection)
        {
            throw new SupportDomainException(
                SupportErrorCodes.ReplyConnectionRequired, "Choose the WhatsApp connection the template goes out through.");
        }

        if (!await channels.IsTemplateApprovedAsync(ticket.TenantId, connection, cancellationToken))
        {
            throw new SupportDomainException(
                SupportErrorCodes.ReplyTemplateNotApproved, "The connection does not have the support reply template approved.");
        }

        return new SupportReplyRoute(
            SupportReplyDelivery.Template, null, connection, ticket.Requester.MobilePhone.Trim().TrimStart('+'));
    }

    private async Task<SupportChannelSendResult> SendAsync(
        SupportTicket ticket, SupportReplyRoute route, SupportTicketReply reply, Guid actor, Guid? uploadId, string? fileName,
        CancellationToken cancellationToken) => route.Delivery switch
        {
            SupportReplyDelivery.Text => await channels.SendThreadAsync(
                new SupportChannelThreadSend(ticket.TenantId, route.ConversationId!.Value, reply.Id, actor, reply.Text, uploadId, fileName),
                cancellationToken),
            SupportReplyDelivery.Template => await channels.SendTemplateAsync(
                new SupportChannelTemplateSend(
                    ticket.TenantId, route.ConversationId, route.ConnectionId, route.WaId, ticket.CustomerId,
                    await RecipientNameAsync(ticket, cancellationToken), reply.Id, actor, ticket.DisplayNumber, reply.Text),
                cancellationToken),
            SupportReplyDelivery.Email => await email.SendAsync(
                new SupportReplyEmail(
                    ticket.Requester!.Email!,
                    await tenantDirectory.GetDisplayNameAsync(new TenantId(ticket.TenantId), cancellationToken) ?? string.Empty,
                    ticket.DisplayNumber,
                    ticket.Subject,
                    reply.Text),
                cancellationToken),
            _ => throw new InvalidOperationException($"Unknown reply delivery '{route.Delivery}'."),
        };

    /// <summary>
    /// Guarda lo que pasó en una transacción. El mensaje ya salió, así que una versión que cambió mientras tanto no lo deshace: se recarga
    /// el ticket y la fila y se aplica otra vez, una sola vez más.
    /// </summary>
    private async Task<(SupportTicket Ticket, Guid? LinkedConversation)> ApplyOutcomeAsync(
        SupportTicket ticket, SupportTicketReply reply, SupportChannelSendResult outcome, Guid actor, bool resolve,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ApplyOnceAsync(ticket, reply, outcome, actor, resolve, cancellationToken);
            }
            catch (RequestConcurrencyException) when (attempt == 0)
            {
                unitOfWork.DiscardChanges();
                ticket = await tickets.FindAsync(ticket.TenantId, ticket.Id, cancellationToken)
                    ?? throw new InvalidOperationException($"Ticket '{ticket.Id}' disappeared while replying.");
                reply = await replies.FindByClientIdAsync(ticket.TenantId, ticket.Id, reply.ClientId, cancellationToken)
                    ?? throw new InvalidOperationException($"Reply '{reply.Id}' disappeared while replying.");
            }
        }
    }

    private async Task<(SupportTicket Ticket, Guid? LinkedConversation)> ApplyOnceAsync(
        SupportTicket ticket, SupportTicketReply reply, SupportChannelSendResult outcome, Guid actor, bool resolve,
        CancellationToken cancellationToken)
    {
        // Otro request con el mismo clientId (o la recarga después de un choque de versión) ya la dejó terminada: él escribió la traza,
        // la auditoría y el Resuelto. Repetirlos daría dos ReplySent por una respuesta.
        if (reply.Status is not SupportReplyStatus.Pending)
        {
            return (ticket, null);
        }

        // El reloj se lee después del envío: lo que se escribe acá va después del mensaje en el hilo.
        var context = await composer.ReadContextAsync(ticket.TenantId, cancellationToken);
        var now = context.Now;
        Guid? linkedConversation = null;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        switch (outcome)
        {
            case SupportChannelSendResult.Sent sent:
                reply.MarkSent(sent.MessageId, now);
                break;
            case SupportChannelSendResult.Failed failed:
                reply.MarkFailed(failed.Code, now);
                break;
        }

        if (outcome is SupportChannelSendResult.Sent { ConversationId: { } conversationId }
            && ticket.ConversationId is null
            && reply.Delivery is SupportReplyDelivery.Template)
        {
            // Plan E14: la conversación que abrió la plantilla queda en el ticket, que la sigue desde ya.
            ticket.LinkConversation(new SupportConversationRef(conversationId, SupportConversationRef.WhatsApp), actor, now);
            await conversationEvents.AppendAsync(
                ticket.TenantId, conversationId, opened: true, ticket.Id.Value, actor, now + EventOffset, cancellationToken);
            linkedConversation = conversationId;
        }

        if (outcome is SupportChannelSendResult.Sent { MessageId: { } messageId } && ticket.ConversationId is { } ticketConversation)
        {
            await PinAsync(ticket, ticketConversation, messageId, now, cancellationToken);
        }

        ticket.RecordReply(reply, now);
        auditPublisher.Publish(
            ticket.TenantId,
            executionContext.SubjectId,
            outcome is SupportChannelSendResult.Sent ? SupportAuditActions.TicketReplySent : SupportAuditActions.TicketReplyFailed,
            SupportAuditActions.Resources.Ticket,
            ticket.Id.ToString(),
            outcome is SupportChannelSendResult.Sent ? "success" : "failure",
            ReplyFields,
            now);

        // «Enviar y marcar Resuelto» sólo si salió (plan E17), y sólo si el estado lo permite: uno ya resuelto se queda como está.
        if (resolve && outcome is SupportChannelSendResult.Sent
            && SupportTicketStatuses.AllowedFrom(ticket.Status).Contains(SupportTicketStatus.Resolved))
        {
            ticket.ChangeStatus(SupportTicketStatus.Resolved, actor, now, context.Clock);
            auditPublisher.Publish(
                ticket.TenantId,
                executionContext.SubjectId,
                SupportAuditActions.TicketStatusChanged,
                SupportAuditActions.Resources.Ticket,
                ticket.Id.ToString(),
                "success",
                StatusFields,
                now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (ticket, linkedConversation);
    }

    // El mensaje que salió entra al expediente fijado, con su hora real si Mensajería ya la tiene.
    private async Task PinAsync(
        SupportTicket ticket, Guid conversationId, Guid messageId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var link = await links.FindAsync(ticket.TenantId, ticket.Id, messageId, cancellationToken);
        if (link is not null)
        {
            link.Pin(now);
            return;
        }

        var message = (await conversations.FindMessagesAsync(ticket.TenantId, conversationId, [messageId], cancellationToken))
            .SingleOrDefault();
        links.Add(SupportTicketMessage.Link(
            ticket.TenantId, ticket.Id, conversationId, messageId, message?.OccurredAt ?? now, SupportTicketMessageState.Pinned, now));
    }

    // El nombre de la foto y, si no tiene, el del cliente vinculado: llena «nombre_contacto» de la plantilla.
    private async Task<string> RecipientNameAsync(SupportTicket ticket, CancellationToken cancellationToken)
    {
        var fromRequester = ticket.Requester?.DisplayName();
        if (!string.IsNullOrWhiteSpace(fromRequester))
        {
            return fromRequester;
        }

        return ticket.CustomerId is { } customerId
            && await customers.FindAsync(ticket.TenantId, customerId, cancellationToken) is { } customer
                ? customer.Name
                : string.Empty;
    }

    private async Task<SupportReplyResultDto> ResultAsync(SupportTicket ticket, Guid replyId, CancellationToken cancellationToken)
    {
        var detail = await composer.ComposeDetailAsync(ticket, cancellationToken);
        return new SupportReplyResultDto(detail.Replies.Single(reply => reply.Id == replyId), detail);
    }

    private static SupportDomainException Unavailable(SupportReplyChannel channel) =>
        new(SupportErrorCodes.ReplyChannelUnavailable, $"The reply channel '{channel}' is not available for this ticket.");
}
