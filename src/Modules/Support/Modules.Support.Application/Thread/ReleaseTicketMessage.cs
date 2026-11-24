using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <param name="ExpectedVersion">La versión del ticket que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record ReleaseTicketMessageCommand(Guid TenantId, Guid TicketId, Guid MessageId, long ExpectedVersion)
    : ICommand<SupportTicketDetailDto>;

/// <summary>
/// Soltar un mensaje del expediente (plan E9): sale de la página y el seguimiento del hilo nunca lo vuelve a vincular (la fila queda
/// <see cref="SupportTicketMessageState.Released"/>). Sólo un mensaje que tiene fila en este ticket: si no, 422
/// <c>support.message.not_linked</c>. Soltar uno ya soltado no hace nada, salvo en un ticket cerrado, que
/// responde 422 <c>support.ticket.not_editable</c> antes de mirar nada más.
/// </summary>
public sealed class ReleaseTicketMessageHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    ISupportTicketMessageRepository links,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer)
    : ICommandHandler<ReleaseTicketMessageCommand, SupportTicketDetailDto>
{
    private static readonly string[] ChangedFields = ["messages"];

    public async Task<SupportTicketDetailDto> HandleAsync(ReleaseTicketMessageCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);
        var (ticket, actor) = await scope.ManageAsync(command.TenantId, command.TicketId, cancellationToken);
        SupportTicketScope.EnsureExternal(ticket);
        SupportTicketScope.EnsureVersion(ticket, command.ExpectedVersion);

        // Block E (E-2): antes que cualquier otra regla y que el no-op de abajo; un cerrado es 422 aunque el mensaje ya esté suelto.
        ticket.EnsureMessagesCanChange();
        var link = await links.FindAsync(command.TenantId, ticket.Id, command.MessageId, cancellationToken)
            ?? throw new SupportDomainException(SupportErrorCodes.MessageNotLinked, "The message is not linked to this ticket.");

        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        if (link.State is SupportTicketMessageState.Released)
        {
            return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
        }

        var now = context.Now;
        var version = ticket.Version;
        link.Release(now);
        ticket.RecordMessageEvent(SupportTicketEventKind.MessageReleased, link.MessageId, actor, now);
        await SupportTicketActions.SaveIfChangedAsync(
            ticket, version, executionContext, auditPublisher, unitOfWork, SupportAuditActions.TicketMessageReleased, ChangedFields, now,
            cancellationToken);
        if (ticket.Version != version)
        {
            await realtime.TicketChangedAsync(command.TenantId, ticket.Id.Value, CancellationToken.None);
        }

        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }
}
