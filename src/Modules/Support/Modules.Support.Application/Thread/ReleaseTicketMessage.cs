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
/// <c>support.message.not_linked</c>. Soltar uno ya soltado no hace nada.
/// </summary>
public sealed class ReleaseTicketMessageHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    ISupportTicketRepository tickets,
    ISupportTicketMessageRepository links,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    SupportTicketDtoComposer composer)
    : ICommandHandler<ReleaseTicketMessageCommand, SupportTicketDetailDto>
{
    private static readonly string[] ChangedFields = ["messages"];

    public async Task<SupportTicketDetailDto> HandleAsync(ReleaseTicketMessageCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.Manage, cancellationToken);
        var (ticket, actor) = await SupportTicketActions.LoadAsync(
            executionContext, membershipDirectory, tickets, command.TenantId, command.TicketId, command.ExpectedVersion, cancellationToken);
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
        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }
}
