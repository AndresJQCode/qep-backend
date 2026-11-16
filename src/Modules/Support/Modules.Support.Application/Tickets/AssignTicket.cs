using BuildingBlocks.Application;
using FluentValidation;
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

/// <summary>Asigna o quita el responsable, en cualquier estado (pre-flight R9: también resuelto o cerrado, sin cambiar el estado).
/// El responsable es una membresía activa con <c>support.ticket.manage</c>.</summary>
public sealed class AssignTicketHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    ISupportTicketRepository tickets,
    ISupportMemberDirectory members,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer,
    IValidator<AssignTicketCommand> validator)
    : ICommandHandler<AssignTicketCommand, SupportTicketDetailDto>
{
    private static readonly string[] ChangedFields = ["assigneeMemberId"];

    public async Task<SupportTicketDetailDto> HandleAsync(AssignTicketCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var (ticket, actor) = await SupportTicketActions.LoadAsync(
            executionContext, membershipDirectory, tickets, command.TenantId, command.TicketId, command.ExpectedVersion, cancellationToken);
        await SupportTicketRules.EnsureAssigneeAsync(members, command.TenantId, command.AssigneeMemberId, cancellationToken);
        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var now = context.Now;
        var version = ticket.Version;
        ticket.Assign(command.AssigneeMemberId, actor, now);
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
