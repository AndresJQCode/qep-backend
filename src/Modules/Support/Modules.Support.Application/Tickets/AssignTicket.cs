using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary><c>AssigneeMemberId</c> null deja el ticket sin responsable.</summary>
public sealed record AssignTicketRequest(Guid? AssigneeMemberId);

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record AssignTicketCommand(Guid TenantId, Guid TicketId, Guid? AssigneeMemberId, long ExpectedVersion)
    : ICommand<SupportTicketDetailDto>
{
    public static AssignTicketCommand From(Guid tenantId, Guid ticketId, AssignTicketRequest request, long expectedVersion) =>
        new(tenantId, ticketId, request.AssigneeMemberId, expectedVersion);
}

public sealed class AssignTicketValidator : AbstractValidator<AssignTicketCommand>
{
    public AssignTicketValidator()
    {
        RuleFor(command => command.AssigneeMemberId).NotEqual(Guid.Empty);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>Asigna o quita el responsable. Un ticket resuelto o cerrado lo rechaza con <c>422 support.ticket.not_editable</c>, también
/// con el mismo valor (R9, owner 2026-11-15, en <c>SupportTicket.EnsureEditable</c>). El responsable es una membresía activa con
/// <c>support.ticket.manage</c>. «Asignármelo» es <see cref="TakeTicketHandler"/>.</summary>
public sealed class AssignTicketHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    ISupportMemberDirectory members,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    ISupportIntegrationEvents integrationEvents,
    SupportTicketDtoComposer composer,
    IValidator<AssignTicketCommand> validator)
    : ICommandHandler<AssignTicketCommand, SupportTicketDetailDto>
{
    private static readonly string[] ChangedFields = ["assigneeMemberId"];

    public async Task<SupportTicketDetailDto> HandleAsync(AssignTicketCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var (ticket, actor) = await scope.ActAsync(command.TenantId, command.TicketId, command.ExpectedVersion, cancellationToken);
        // Una tarea la puede recibir quien usa tareas; un externo, sólo quien gestiona (spec 2026-11-22, D19).
        if (ticket.IsInternal)
        {
            // Spec §5.2.1: the assignee of a task is mandatory, so it cannot be left without one.
            var taskAssignee = command.AssigneeMemberId
                ?? throw new SupportDomainException(SupportErrorCodes.AssigneeInvalid, "A task must have an assignee.");
            await SupportTicketRules.EnsureTaskAssigneeAsync(members, command.TenantId, taskAssignee, cancellationToken);
        }
        else
        {
            await SupportTicketRules.EnsureAssigneeAsync(members, command.TenantId, command.AssigneeMemberId, cancellationToken);
        }

        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var now = context.Now;
        var version = ticket.Version;
        var formerAssignee = ticket.AssigneeMemberId;
        ticket.Assign(command.AssigneeMemberId, actor, now);
        if (ticket.Version != version)
        {
            // Both events go to the outbox in the same transaction as the save. The new assignee hears about it unless they took it
            // themselves; tasks and customer tickets each have their own event (task-notices spec D-7).
            if (command.AssigneeMemberId is { } newAssignee && newAssignee != actor)
            {
                if (ticket.IsInternal)
                {
                    integrationEvents.PublishTaskAssigned(ticket, actor, now);
                }
                else
                {
                    integrationEvents.PublishTicketAssigned(ticket, actor, now);
                }
            }

            // The former assignee is released whenever the ticket leaves their hands, also when it is left unassigned or kept by the actor.
            if (formerAssignee is { } former && former != ticket.AssigneeMemberId)
            {
                integrationEvents.PublishAssigneeReleased(ticket, former, SupportAssigneeReleaseReasons.Reassigned, now);
            }
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
