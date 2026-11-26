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
    ISupportIntegrationEvents integrationEvents,
    SupportTicketDtoComposer composer)
    : ICommandHandler<TakeTicketCommand, SupportTicketDetailDto>
{
    private static readonly string[] ChangedFields = ["assigneeMemberId"];

    public async Task<SupportTicketDetailDto> HandleAsync(TakeTicketCommand command, CancellationToken cancellationToken)
    {
        var (ticket, actor) = await scope.ActAsync(command.TenantId, command.TicketId, command.ExpectedVersion, cancellationToken);
        // Quien toma una tarea sólo necesita usar tareas; un externo exige gestionar (spec 2026-11-22, D19). Tomar nunca avisa: el
        // responsable es quien actúa.
        if (ticket.IsInternal)
        {
            await SupportTicketRules.EnsureTaskAssigneeAsync(members, command.TenantId, actor, cancellationToken);
        }
        else
        {
            await SupportTicketRules.EnsureAssigneeAsync(members, command.TenantId, actor, cancellationToken);
        }

        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var now = context.Now;
        var version = ticket.Version;
        var formerAssignee = ticket.AssigneeMemberId;
        ticket.Assign(actor, actor, now);
        // Taking is never told to the one who takes, but whoever had it is released (task-notices spec D-7).
        if (ticket.Version != version && formerAssignee is { } former && former != actor)
        {
            integrationEvents.PublishAssigneeReleased(ticket, former, SupportAssigneeReleaseReasons.Reassigned, now);
        }

        await SupportTicketActions.SaveIfChangedAsync(
            ticket, version, executionContext, auditPublisher, unitOfWork, SupportAuditActions.TicketAssigned, ChangedFields, now,
            cancellationToken);
        if (ticket.Version != version)
        {
            await SupportTicketNotices.ChangedAsync(realtime, ticket, formerAssignee, CancellationToken.None);
        }

        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }
}
