using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary><c>DueOn</c> es <c>yyyy-MM-dd</c>; <c>null</c> quita la fecha de la tarea (spec 2026-11-22, D20).</summary>
public sealed record ChangeTicketDueDateRequest(string? DueOn);

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record ChangeTicketDueDateCommand(Guid TenantId, Guid TicketId, string? DueOn, long ExpectedVersion)
    : ICommand<SupportTicketDetailDto>
{
    public static ChangeTicketDueDateCommand From(Guid tenantId, Guid ticketId, ChangeTicketDueDateRequest request, long expectedVersion) =>
        new(tenantId, ticketId, request.DueOn, expectedVersion);
}

public sealed class ChangeTicketDueDateValidator : AbstractValidator<ChangeTicketDueDateCommand>
{
    public ChangeTicketDueDateValidator()
    {
        // Sólo la forma: que no sea anterior a hoy en la zona del tenant lo juzga el dominio (internal_due_in_past).
        RuleFor(command => command.DueOn)
            .Must(text => text is null || GetSupportReportValidator.TryParse(text, out _))
            .WithMessage("DueOn is a date as YYYY-MM-DD, or null to remove it.");
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Cambia la fecha límite libre de una tarea (spec 2026-11-22 §5.2). La puerta decide quién puede: el creador, el responsable o quien
/// gestiona; sobre un externo es 422 <c>support.ticket.not_internal</c> (lo juzga el dominio), resuelta o cerrada es
/// <c>not_editable</c>, y la misma fecha no escribe nada. Audita sólo el nombre del campo y avisa al tiempo real si cambió algo.
/// </summary>
public sealed class ChangeTicketDueDateHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer,
    IValidator<ChangeTicketDueDateCommand> validator)
    : ICommandHandler<ChangeTicketDueDateCommand, SupportTicketDetailDto>
{
    private static readonly string[] ChangedFields = ["dueOn"];

    public async Task<SupportTicketDetailDto> HandleAsync(ChangeTicketDueDateCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var (ticket, actor) = await scope.ActAsync(command.TenantId, command.TicketId, command.ExpectedVersion, cancellationToken);
        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var now = context.Now;
        // Ya validada la forma: TryParse no falla acá.
        DateOnly? dueOn = command.DueOn is { } text && GetSupportReportValidator.TryParse(text, out var date) ? date : null;
        var version = ticket.Version;
        ticket.ChangeDueOn(dueOn, actor, now, context.Clock);
        await SupportTicketActions.SaveIfChangedAsync(
            ticket, version, executionContext, auditPublisher, unitOfWork, SupportAuditActions.TicketDueDateChanged, ChangedFields, now,
            cancellationToken);
        if (ticket.Version != version)
        {
            await SupportTicketNotices.ChangedAsync(realtime, ticket, formerAssigneeMemberId: null, CancellationToken.None);
        }

        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }
}
