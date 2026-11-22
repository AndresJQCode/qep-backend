using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <param name="ExpectedVersion">La versión del ticket que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record PinTicketMessageCommand(Guid TenantId, Guid TicketId, Guid MessageId, long ExpectedVersion)
    : ICommand<SupportTicketDetailDto>;

/// <summary>
/// Fijar un mensaje en el expediente (plan E9): cualquier mensaje de la conversación del ticket, entrante o saliente, también anterior al
/// ticket o uno que se había soltado. Ticket sin conversación: 422 <c>support.ticket.no_conversation</c>; mensaje de otra conversación:
/// 422 <c>support.message.not_in_conversation</c>. Fijar uno ya fijado no hace nada (ni evento ni versión), salvo en un
/// ticket cerrado, que responde 422 <c>support.ticket.not_editable</c> antes de mirar nada más. Fijar un entrante que
/// esperaba en «sin clasificar» lo deja <c>Assigned</c> a este ticket en el mismo guardado (revisión final, M6): el asesor ya decidió.
/// </summary>
public sealed class PinTicketMessageHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    ISupportTicketMessageRepository links,
    ISupportUnclassifiedRepository unclassified,
    ISupportConversationDirectory conversations,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer)
    : ICommandHandler<PinTicketMessageCommand, SupportTicketDetailDto>
{
    private static readonly string[] ChangedFields = ["messages"];

    public async Task<SupportTicketDetailDto> HandleAsync(PinTicketMessageCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);
        var (ticket, actor) = await scope.ManageAsync(command.TenantId, command.TicketId, cancellationToken);
        SupportTicketScope.EnsureExternal(ticket);
        SupportTicketScope.EnsureVersion(ticket, command.ExpectedVersion);

        // Block E (E-2): antes que cualquier otra regla y que el no-op de abajo; un cerrado es 422 aunque el mensaje ya esté fijado.
        ticket.EnsureMessagesCanChange();
        if (ticket.ConversationId is not { } conversationId)
        {
            throw new SupportDomainException(SupportErrorCodes.NoConversation, "The ticket has no conversation to pin messages from.");
        }

        var message = (await conversations.FindMessagesAsync(command.TenantId, conversationId, [command.MessageId], cancellationToken))
            .SingleOrDefault()
            ?? throw new SupportDomainException(
                SupportErrorCodes.MessageNotInConversation, "The message is not part of the ticket conversation.");

        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var now = context.Now;
        var link = await links.FindAsync(command.TenantId, ticket.Id, message.Id, cancellationToken);
        if (link is { State: SupportTicketMessageState.Pinned })
        {
            return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
        }

        if (link is null)
        {
            links.Add(SupportTicketMessage.Link(
                command.TenantId, ticket.Id, conversationId, message.Id, message.OccurredAt, SupportTicketMessageState.Pinned, now));
        }
        else
        {
            link.Pin(now);
        }

        if (await unclassified.FindAsync(command.TenantId, message.Id, cancellationToken) is { IsPending: true } pending
            && pending.ConversationId == conversationId)
        {
            pending.Assign(ticket.Id, actor, now);
        }

        var version = ticket.Version;
        ticket.RecordMessageEvent(SupportTicketEventKind.MessagePinned, message.Id, actor, now);
        await SupportTicketActions.SaveIfChangedAsync(
            ticket, version, executionContext, auditPublisher, unitOfWork, SupportAuditActions.TicketMessagePinned, ChangedFields, now,
            cancellationToken);
        if (ticket.Version != version)
        {
            await realtime.TicketChangedAsync(command.TenantId, ticket.Id.Value, CancellationToken.None);
        }

        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }
}
