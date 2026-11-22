using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record TakeTicketCommand(Guid TenantId, Guid TicketId, long ExpectedVersion) : ICommand<SupportTicketDetailDto>;

/// <summary>
/// Fase 4a (spec §12, plan G7): «Asignármelo». El responsable es quien llama, resuelto por la sesión y validado con la misma regla de
/// <see cref="AssignTicketHandler"/> (membresía activa con <c>support.ticket.manage</c>); el orden y la auditoría son los de esa
/// acción. Ya asignado a quien llama, no cambia nada: ni versión, ni evento, ni auditoría, ni aviso. Resuelto o cerrado responde
/// <c>422 support.ticket.not_editable</c> (R9, en el dominio). La ruta copia <c>POST /conversations/{id}/take</c> de Mensajería.
/// </summary>
public sealed class TakeTicketHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    ISupportMemberDirectory members,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer)
    : ICommandHandler<TakeTicketCommand, SupportTicketDetailDto>
{
    private static readonly string[] ChangedFields = ["assigneeMemberId"];

    public async Task<SupportTicketDetailDto> HandleAsync(TakeTicketCommand command, CancellationToken cancellationToken)
    {
        var (ticket, actor) = await scope.ActAsync(command.TenantId, command.TicketId, command.ExpectedVersion, cancellationToken);
        await SupportTicketRules.EnsureAssigneeAsync(members, command.TenantId, actor, cancellationToken);
        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var now = context.Now;
        var version = ticket.Version;
        ticket.Assign(actor, actor, now);
        await SupportTicketActions.SaveIfChangedAsync(
            ticket, version, executionContext, auditPublisher, unitOfWork, SupportAuditActions.TicketAssigned, ChangedFields, now,
            cancellationToken);
        if (ticket.Version != version)
        {
            await realtime.TicketChangedAsync(command.TenantId, ticket.Id.Value, CancellationToken.None);
        }

        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }
}
