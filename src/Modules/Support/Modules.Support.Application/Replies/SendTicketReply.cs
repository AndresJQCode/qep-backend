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
/// sesión de subida de Mensajería (de su extensión sale la clave del staging). <c>Resolve</c> es «Enviar y marcar Resuelto». Fase 4b
/// (H1, H2): con <c>Formal</c> la respuesta lleva el PDF de la respuesta formal; <c>Text</c> sigue siendo el mensaje del canal.</summary>
public sealed record SupportReplyRequest(
    Guid? ClientId,
    string? Channel,
    string? Text,
    Guid? ConnectionId,
    Guid? UploadId,
    bool Resolve,
    string? FileName = null,
    SupportFormalReplyRequest? Formal = null);

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
    string? FileName = null,
    SupportFormalReplyRequest? Formal = null)
    : ICommand<SupportReplyResultDto>
{
    public static SendTicketReplyCommand From(Guid tenantId, Guid ticketId, SupportReplyRequest request, long expectedVersion) => new(
        tenantId, ticketId, request.ClientId, request.Channel, request.Text, request.ConnectionId, request.UploadId, request.Resolve,
        expectedVersion, request.FileName, request.Formal);
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
            .Must(text => SupportTicketRules.TryParseExact<SupportReplyChannel>(text, out var channel) && channel is not SupportReplyChannel.None)
            .WithMessage("Channel must be Thread, WhatsAppTemplate, Email or Portal.");
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

        // Fase 4b (H1 a H4, H11): la parte formal con sus reglas, y un solo documento por respuesta.
        RuleFor(command => command.Formal!)
            .SetValidator(new SupportFormalReplyRequestValidator())
            .When(command => command.Formal is not null);
        RuleFor(command => command.UploadId)
            .Null()
            .When(command => command.Formal is not null)
            .WithMessage("Una respuesta formal ya lleva su PDF: no puede llevar otro adjunto.");
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
/// Fase 4b (Tarea 8): con <c>Formal</c>, después de la ruta y antes de la fila, <see cref="SupportFormalReplies"/> genera el PDF y lo guarda
/// en el bucket (H7); la fila del documento entra en el mismo guardado que la respuesta. Una <c>Pending</c> retomada manda el PDF guardado
/// (lo lee del bucket), nunca uno nuevo. Una respuesta nueva que se borra (rechazada antes de enviar, o perdida la carrera del
/// <c>clientId</c>) se lleva su documento por la cascada, y su PDF se borra del bucket, que bajo <c>support/</c> no tiene regla de ciclo
/// de vida (revisión del Bloque B). <c>send_failed</c> como rechazo (el PDF no subió a Meta o no se guardó en R2) es eso mismo: no salió
/// nada, la respuesta se borra y se puede reintentar.
///
/// El <c>id</c> de la fila es el <c>clientId</c> que viaja a Mensajería, que es idempotente por él: un reintento con una fila todavía
/// <c>Pending</c> (se cayó entre el envío y la marca) vuelve a llamar y Mensajería devuelve el mensaje que ya salió. El correo no tiene esa
/// garantía. Una respuesta que el canal rechaza antes de enviar (la ventana se cerró entre la revisión y el envío) borra su fila y
/// responde 422: el hilo no la muestra y la pantalla tampoco. Una <c>Pending</c> retomada no se borra nunca: sale por lo que guardó
/// su fila y, si el canal la rechaza o ya no tiene ruta, queda <c>Failed</c> con el motivo (201). Una que Meta o el correo rechazan queda <c>Failed</c> y responde 201, igual
/// que el hilo.
/// </summary>
public sealed class SendTicketReplyHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantDirectory tenantDirectory,
    SupportTicketScope scope,
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
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer,
    IValidator<SendTicketReplyCommand> validator,
    SupportFormalReplies formalReplies,
    ISupportDocumentStorage documentStorage,
    ISupportIntegrationEvents integrationEvents)
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
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        if (!SupportTicketRules.TryParseExact<SupportReplyChannel>(command.Channel, out var channel))
        {
            throw new InvalidOperationException($"Channel '{command.Channel}' passed validation but is not a reply channel.");
        }

        var (ticket, actor) = await scope.ManageAsync(command.TenantId, command.TicketId, cancellationToken);
        // Spec 2026-11-22 (tareas internas): una tarea no tiene a quién responder. Antes del clientId, para que ni un reintento
        // cuente con una fila de respuesta en un interno.
        SupportTicketScope.EnsureExternal(ticket);

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

        // Revisión del bloque C (CM1 y CM2): una Pending vieja sale por lo que guardó su fila (canal, entrega, conexión y texto), nunca por
        // lo que traiga el reintento. Si ya no tiene por dónde salir, queda Failed con el motivo: no se borra ni se queda Pending.
        // Fase 4b: también su documento formal, si lo tiene (Review Focus 1).
        var storedDocument = existing is null
            ? null
            : await replies.FindDocumentAsync(command.TenantId, ticket.Id, existing.Id, cancellationToken);
        SupportReplyRoute route;
        if (existing is not null)
        {
            try
            {
                route = await ResumeRouteAsync(ticket, existing, cancellationToken);
            }
            catch (SupportDomainException unavailable)
            {
                return await FailResumedAsync(ticket, existing, unavailable.Code, actor, storedDocument is not null, cancellationToken);
            }
        }
        else
        {
            route = await RouteAsync(ticket, channel, command, command.Text!.Trim(), context.Now, cancellationToken);
        }

        // H7: el PDF se genera y se guarda antes de la fila; si falla, no queda nada (el generador responde 422).
        var prepared = existing is null && command.Formal is { } formal
            ? await formalReplies.PrepareAsync(ticket, route.Delivery, command.Text!.Trim(), formal, context, cancellationToken)
            : null;

        var reply = existing;
        SupportFormalPayload? document;
        if (reply is null)
        {
            var text = command.Text!.Trim();
            try
            {
                // Revisión final (I1 y M2): la fila guarda el destino de la plantilla al celular y el adjunto, para que un retomado mande
                // exactamente esto.
                reply = SupportTicketReply.Start(
                    command.TenantId, ticket.Id, clientId, channel, route.Delivery, text, route.ConnectionId, actor, context.Now,
                    route.WaId, command.UploadId, command.FileName?.Trim());
                replies.Add(reply);
                document = null;
                if (prepared is not null)
                {
                    var row = SupportReplyDocument.Create(
                        reply, prepared.Decision, prepared.NextStep, prepared.SignerMemberId, prepared.SignerName, prepared.SignerTitle,
                        prepared.Annexes, prepared.StorageKey, prepared.FileName, prepared.Content, prepared.RespondedOn, context.Now);
                    replies.AddDocument(row);
                    document = new SupportFormalPayload(prepared.Content, prepared.FileName, row.DocumentClientId);
                }

                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            // Dos envíos del mismo clientId a la vez: el segundo devuelve la fila del primero, sin enviar. Su PDF no quedó en ninguna fila.
            catch (ResourceConflictException conflict) when (conflict.Code == SupportErrorCodes.ReplyDuplicateClientId)
            {
                unitOfWork.DiscardChanges();
                await DiscardDocumentAsync(prepared);
                var winner = await replies.FindByClientIdAsync(command.TenantId, ticket.Id, clientId, cancellationToken)
                    ?? throw new InvalidOperationException("The reply that won the client id race was not found.");
                var current = await tickets.FindAsync(command.TenantId, ticket.Id, cancellationToken) ?? ticket;
                return await ResultAsync(current, winner.Id, cancellationToken);
            }
            catch (Exception) when (prepared is not null)
            {
                // H7: el PDF ya estaba en el bucket y la fila no se guardó. Revisión del bloque C: salvo que el commit sí se haya confirmado
                // y lo que falló fue la vuelta (la conexión se cayó al responder): entonces la fila existe y el PDF es su documento.
                if (!await ReplySavedAsync(command.TenantId, ticket.Id, clientId))
                {
                    await DiscardDocumentAsync(prepared);
                }

                throw;
            }
        }
        else
        {
            // Review Focus 1: el retomado manda el PDF guardado y su DocumentClientId; el generador no se vuelve a llamar.
            document = storedDocument is null
                ? null
                : new SupportFormalPayload(
                    await documentStorage.ReadAsync(storedDocument.StorageKey, cancellationToken),
                    storedDocument.FileName,
                    storedDocument.DocumentClientId);
        }

        // El adjunto sale de la fila: un retomado manda el que guardó (o ninguno), nunca el que traiga el reintento.
        var outcome = await SendAsync(ticket, route, reply, actor, document, cancellationToken);
        if (outcome is SupportChannelSendResult.Rejected rejected)
        {
            if (existing is not null)
            {
                return await FailResumedAsync(ticket, reply, rejected.Reason, actor, document is not null, cancellationToken);
            }

            // Plan E17 y H9: la respuesta nunca existió para el solicitante. La fila se va con su documento (cascada) y el PDF sale del
            // bucket (revisión del Bloque B).
            replies.Remove(reply);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await DiscardDocumentAsync(prepared);
            throw new SupportDomainException(SupportReplyRules.CodeFor(rejected.Reason), $"The reply was rejected: {rejected.Reason}.");
        }

        var (saved, linkedConversation) = await ApplyOutcomeAsync(
            ticket, reply, outcome, actor, command.Resolve, document is not null, cancellationToken);
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
        // Plan F12: un ticket anónimo sólo se responde por la página de consulta, y la página sólo responde a uno anónimo. Va primero:
        // Portal no pasa por Mensajería ni por el correo, y antes de esto llegaba a la ruta del hilo y reventaba al crear la fila.
        if (ticket.IsAnonymous != (channel is SupportReplyChannel.Portal))
        {
            throw ticket.IsAnonymous
                ? new SupportDomainException(SupportErrorCodes.ReplyPortalOnly, "An anonymous ticket is answered only on the consult page.")
                : new SupportDomainException(
                    SupportErrorCodes.ReplyPortalUnavailable, "The consult page only answers anonymous tickets.");
        }

        SupportReplyRoute route;
        if (channel is SupportReplyChannel.Portal)
        {
            route = new SupportReplyRoute(SupportReplyDelivery.Portal, null, null, null);
        }
        else if (channel is SupportReplyChannel.Email)
        {
            route = string.IsNullOrWhiteSpace(ticket.Requester?.Email)
                ? throw Unavailable(channel)
                : new SupportReplyRoute(SupportReplyDelivery.Email, null, null, null);
        }
        else
        {
            await TenantModuleGuard.EnsureEnabledAsync(tenantModules, ticket.TenantId, TenantModuleKeys.Messaging, cancellationToken);
            var formal = command.Formal is not null;
            route = channel is SupportReplyChannel.WhatsAppTemplate && ticket.ConversationId is null
                ? await TemplateToPhoneAsync(ticket, command.ConnectionId, formal, cancellationToken)
                : await ThreadRouteAsync(ticket, now, formal, cancellationToken);
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

        // Ruling del controlador (revisión del bloque D): un adjunto de Instagram no lleva pie y el texto de la respuesta se perdería.
        if (command.UploadId is not null && ticket.ConversationChannel != SupportConversationRef.WhatsApp)
        {
            throw new SupportDomainException(
                SupportErrorCodes.ReplyAttachmentUnavailable, "An attachment only goes through a WhatsApp conversation.");
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

    /// <summary>
    /// CM1: la ruta de una Pending vieja sale de su fila. La entrega guardada manda: <c>Text</c> va al hilo del ticket aunque la ventana se
    /// haya cerrado (Mensajería devuelve lo que ya salió por <c>clientId</c>, o lo rechaza y la fila queda Failed); <c>Template</c> va a la
    /// conversación del ticket o, sin ella, al <c>wa_id</c> y por la conexión guardados (revisión final, I1: sin volver a mirar el
    /// celular de la foto, la conexión ni la aprobación de la plantilla, porque Mensajería, idempotente por <c>clientId</c>, devuelve
    /// primero lo que ya salió); <c>Email</c>, al correo de la foto. Lo que ya no se puede es una <see cref="SupportDomainException"/>,
    /// que quien llama convierte en Failed. Sin Mensajería prendida responde 403 y la fila sigue Pending: prender el módulo y reintentar
    /// la retoma.
    /// </summary>
    private async Task<SupportReplyRoute> ResumeRouteAsync(
        SupportTicket ticket, SupportTicketReply stored, CancellationToken cancellationToken)
    {
        if (stored.Delivery is SupportReplyDelivery.Portal)
        {
            return new SupportReplyRoute(SupportReplyDelivery.Portal, null, null, null);
        }

        if (stored.Delivery is SupportReplyDelivery.Email)
        {
            return string.IsNullOrWhiteSpace(ticket.Requester?.Email)
                ? throw Unavailable(SupportReplyChannel.Email)
                : new SupportReplyRoute(SupportReplyDelivery.Email, null, null, null);
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, ticket.TenantId, TenantModuleKeys.Messaging, cancellationToken);
        if (ticket.ConversationId is { } conversationId)
        {
            return new SupportReplyRoute(stored.Delivery, conversationId, null, null);
        }

        if (stored.Delivery is not SupportReplyDelivery.Template)
        {
            throw new SupportDomainException(SupportErrorCodes.NoConversation, "The ticket no longer has a conversation to reply through.");
        }

        // Una fila sin destino guardado no tiene a dónde volver a salir (sólo una anterior a AddSupportReplyTargets).
        return stored is { ConnectionId: { } connection, TargetWaId: { } waId }
            ? new SupportReplyRoute(SupportReplyDelivery.Template, null, connection, waId)
            : throw Unavailable(SupportReplyChannel.WhatsAppTemplate);
    }

    /// <summary>CM2: el retomado que no salió deja su fila <c>Failed</c> con el motivo, con su traza y su auditoría, y responde 201 como
    /// cualquier envío fallido. Así el request original que siga en curso encuentra la fila (ya terminada) y no escribe nada más.</summary>
    private async Task<SupportReplyResultDto> FailResumedAsync(
        SupportTicket ticket, SupportTicketReply reply, string code, Guid actor, bool formal, CancellationToken cancellationToken)
    {
        var (saved, _) = await ApplyOutcomeAsync(
            ticket, reply, new SupportChannelSendResult.Failed(null, code), actor, resolve: false, formal, cancellationToken);
        return await ResultAsync(saved, reply.Id, cancellationToken);
    }

    private async Task<SupportReplyRoute> ThreadRouteAsync(
        SupportTicket ticket, DateTimeOffset now, bool formal, CancellationToken cancellationToken)
    {
        var availability = await SupportReplyRules.ThreadAsync(channels, ticket, now, cancellationToken, formal);
        return availability.Route
            ?? throw new SupportDomainException(availability.ErrorCode!, $"The thread cannot take a reply: {availability.Reason}.");
    }

    // Plan E14: sin conversación, la plantilla va al celular de la foto (sin «+») por la conexión que eligió el asesor. Fase 4b: la
    // respuesta formal necesita su propia plantilla aprobada en esa conexión.
    private async Task<SupportReplyRoute> TemplateToPhoneAsync(
        SupportTicket ticket, Guid? connectionId, bool formal, CancellationToken cancellationToken)
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

        var approved = formal
            ? await channels.IsFormalTemplateApprovedAsync(ticket.TenantId, connection, cancellationToken)
            : await channels.IsTemplateApprovedAsync(ticket.TenantId, connection, cancellationToken);
        if (!approved)
        {
            throw new SupportDomainException(
                SupportErrorCodes.ReplyTemplateNotApproved, "The connection does not have the support reply template approved.");
        }

        return new SupportReplyRoute(
            SupportReplyDelivery.Template, null, connection, ticket.Requester.MobilePhone.Trim().TrimStart('+'));
    }

    /// <summary>El PDF de una respuesta formal tal como sale por el canal: los bytes, el nombre y el <c>clientId</c> del segundo mensaje
    /// de Instagram.</summary>
    private sealed record SupportFormalPayload(byte[] Content, string FileName, Guid DocumentClientId);

    // Fase 4b (H10): con documento, el hilo manda el PDF (un documento con pie en WhatsApp; texto y archivo en Instagram), la plantilla es
    // «Respuesta formal» con el PDF como encabezado, el correo lo adjunta y la página de consulta lo publica.
    private async Task<SupportChannelSendResult> SendAsync(
        SupportTicket ticket,
        SupportReplyRoute route,
        SupportTicketReply reply,
        Guid actor,
        SupportFormalPayload? document,
        CancellationToken cancellationToken) =>
        route.Delivery switch
        {
            // Plan F12: la respuesta se publica, no se envía. Queda Sent y la ve quien consulte con el radicado y el PIN.
            SupportReplyDelivery.Portal => new SupportChannelSendResult.Sent(null, null),
            SupportReplyDelivery.Text when document is not null => await channels.SendThreadDocumentAsync(
                new SupportChannelThreadDocumentSend(
                    ticket.TenantId, route.ConversationId!.Value, reply.Id, document.DocumentClientId, actor, reply.Text, document.Content,
                    document.FileName),
                cancellationToken),
            SupportReplyDelivery.Template when document is not null => await channels.SendFormalTemplateAsync(
                new SupportChannelFormalTemplateSend(
                    ticket.TenantId, route.ConversationId, route.ConnectionId, route.WaId, ticket.CustomerId,
                    await RecipientNameAsync(ticket, cancellationToken), reply.Id, actor, ticket.DisplayNumber, reply.Text, document.Content,
                    document.FileName),
                cancellationToken),
            SupportReplyDelivery.Text => await channels.SendThreadAsync(
                new SupportChannelThreadSend(
                    ticket.TenantId, route.ConversationId!.Value, reply.Id, actor, reply.Text, reply.UploadId, reply.FileName),
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
                    reply.Text,
                    document is null ? null : new SupportEmailDocument(document.FileName, document.Content)),
                cancellationToken),
            _ => throw new InvalidOperationException($"Unknown reply delivery '{route.Delivery}'."),
        };

    // Revisión del bloque C: si el guardado que lanzó en realidad confirmó. Mira la base, no lo que quedó en el contexto. Si ni siquiera se
    // puede mirar, se responde que sí: un PDF huérfano en el bucket no rompe nada, borrar el documento de una respuesta que existe sí.
    private async Task<bool> ReplySavedAsync(Guid tenantId, SupportTicketId ticketId, Guid clientId)
    {
        try
        {
            unitOfWork.DiscardChanges();
            return await replies.FindByClientIdAsync(tenantId, ticketId, clientId, CancellationToken.None) is not null;
        }
        catch (Exception)
        {
            return true;
        }
    }

    // Best effort: el puerto no lanza y deja un log si no pudo; un PDF huérfano no rompe nada, y quien llama ya está respondiendo otra cosa.
    private async Task DiscardDocumentAsync(SupportPreparedFormalReply? prepared)
    {
        if (prepared is not null)
        {
            await documentStorage.DeleteAsync(prepared.StorageKey, CancellationToken.None);
        }
    }

    /// <summary>
    /// Guarda lo que pasó en una transacción. El mensaje ya salió, así que una versión que cambió mientras tanto no lo deshace: se recarga
    /// el ticket y la fila y se aplica otra vez, una sola vez más.
    /// </summary>
    private async Task<(SupportTicket Ticket, Guid? LinkedConversation)> ApplyOutcomeAsync(
        SupportTicket ticket, SupportTicketReply reply, SupportChannelSendResult outcome, Guid actor, bool resolve, bool formal,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var applied = await ApplyOnceAsync(ticket, reply, outcome, actor, resolve, formal, cancellationToken);
                // Plan N13: after the commit of the outcome, on the new reply and on a resumed one alike.
                await realtime.TicketChangedAsync(applied.Ticket.TenantId, applied.Ticket.Id.Value, CancellationToken.None);
                return applied;
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
        SupportTicket ticket, SupportTicketReply reply, SupportChannelSendResult outcome, Guid actor, bool resolve, bool formal,
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
                // M4: el mensaje que Mensajería registró como fallido queda en la fila; no se fija en el expediente.
                reply.MarkFailed(failed.Code, failed.MessageId, now);
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

        // Revisión final (M7): se fija sólo si salió por la conversación del ticket (ya con la que vinculó la plantilla); uno que
        // Mensajería mandó por otra no es de este expediente.
        if (outcome is SupportChannelSendResult.Sent { MessageId: { } messageId, ConversationId: { } sentConversation }
            && ticket.ConversationId is { } ticketConversation
            && sentConversation == ticketConversation)
        {
            await PinAsync(ticket, ticketConversation, messageId, now, cancellationToken);

            // Fase 4b (H10): en Instagram el PDF va en un segundo mensaje, que también es del expediente.
            if (outcome is SupportChannelSendResult.Sent { DocumentMessageId: { } documentMessage } && documentMessage != messageId)
            {
                await PinAsync(ticket, ticketConversation, documentMessage, now, cancellationToken);
            }
        }

        ticket.RecordReply(reply, now, formal);
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
            // Same rule as ChangeTicketStatus: resolving releases the assignee, published before the save so it shares the transaction.
            // The allowed-from guard above already excludes Resolved and Closed, so the previous status is never finished here.
            var assignee = ticket.AssigneeMemberId;
            ticket.ChangeStatus(SupportTicketStatus.Resolved, actor, now, context.Clock);
            if (assignee is { } former)
            {
                integrationEvents.PublishAssigneeReleased(ticket, former, SupportAssigneeReleaseReasons.Resolved, now);
            }

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
