using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

public sealed record ChangeTicketPriorityRequest(string? Priority);

/// <param name="Priority">El nombre del cuerpo, exacto; uno que no es <c>Low</c>, <c>Medium</c>, <c>High</c> ni <c>Urgent</c> llega
/// fuera del enum y lo rechaza el validador.</param>
/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record ChangeTicketPriorityCommand(Guid TenantId, Guid TicketId, SupportTicketPriority Priority, long ExpectedVersion)
    : ICommand<SupportTicketDetailDto>
{
    private const SupportTicketPriority Unknown = (SupportTicketPriority)(-1);

    public static ChangeTicketPriorityCommand From(
        Guid tenantId, Guid ticketId, ChangeTicketPriorityRequest request, long expectedVersion) =>
        new(
            tenantId,
            ticketId,
            SupportTicketRules.TryParseExact<SupportTicketPriority>(request.Priority, out var priority) ? priority : Unknown,
            expectedVersion);
}

public sealed class ChangeTicketPriorityValidator : AbstractValidator<ChangeTicketPriorityCommand>
{
    public ChangeTicketPriorityValidator()
    {
        RuleFor(command => command.Priority).IsInEnum().WithMessage(SupportPriorityRules.Message);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>Cambia la prioridad (spec 2026-11-15 §5.1). No mueve el plazo; resuelto o cerrado, 422 <c>support.ticket.not_editable</c>; la
/// misma prioridad no escribe nada.</summary>
public sealed class ChangeTicketPriorityHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer,
    IValidator<ChangeTicketPriorityCommand> validator)
    : ICommandHandler<ChangeTicketPriorityCommand, SupportTicketDetailDto>
{
    private static readonly string[] ChangedFields = ["priority"];

    public async Task<SupportTicketDetailDto> HandleAsync(ChangeTicketPriorityCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var (ticket, actor) = await scope.ActAsync(command.TenantId, command.TicketId, command.ExpectedVersion, cancellationToken);
        // El contexto lo pide el detalle de todos modos; de él sale la hora del evento.
        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var now = context.Now;
        var version = ticket.Version;
        ticket.ChangePriority(command.Priority, actor, now);
        await SupportTicketActions.SaveIfChangedAsync(
            ticket, version, executionContext, auditPublisher, unitOfWork, SupportAuditActions.TicketPriorityChanged, ChangedFields, now,
            cancellationToken);
        if (ticket.Version != version)
        {
            await SupportTicketNotices.ChangedAsync(realtime, ticket, formerAssigneeMemberId: null, CancellationToken.None);
        }

        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }
}
