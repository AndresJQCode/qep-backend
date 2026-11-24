using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

public sealed record ChangeTicketStatusRequest(string? Status);

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record ChangeTicketStatusCommand(Guid TenantId, Guid TicketId, string Status, long ExpectedVersion)
    : ICommand<SupportTicketDetailDto>
{
    public static ChangeTicketStatusCommand From(Guid tenantId, Guid ticketId, ChangeTicketStatusRequest request, long expectedVersion) =>
        new(tenantId, ticketId, request.Status ?? string.Empty, expectedVersion);
}

public sealed class ChangeTicketStatusValidator : AbstractValidator<ChangeTicketStatusCommand>
{
    public ChangeTicketStatusValidator()
    {
        RuleFor(command => command.Status)
            .Must(text => SupportTicketRules.TryParseExact<SupportTicketStatus>(text, out _))
            .WithMessage("Status must be New, Open, WaitingOnCustomer, WaitingInternal, Resolved or Closed.");
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>Mueve el ticket por la máquina de estados (spec §7.1): fuera de la tabla, 422 <c>support.ticket.transition_invalid</c>.
/// El dominio detiene y reanuda el reloj, y cuenta los ciclos de resolución. Fase 2 (plan E11): cerrar un ticket con conversación
/// escribe <c>TicketClosed</c> en el hilo en la misma transacción, y avisa al tiempo real de Mensajería después de confirmar.</summary>
public sealed class ChangeTicketStatusHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    ISupportIntegrationEvents integrationEvents,
    ISupportConversationEvents conversationEvents,
    SupportTicketDtoComposer composer,
    IValidator<ChangeTicketStatusCommand> validator)
    : ICommandHandler<ChangeTicketStatusCommand, SupportTicketDetailDto>
{
    private static readonly string[] ChangedFields = ["status"];

    public async Task<SupportTicketDetailDto> HandleAsync(ChangeTicketStatusCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        if (!SupportTicketRules.TryParseExact<SupportTicketStatus>(command.Status, out var status))
        {
            throw new InvalidOperationException($"Status '{command.Status}' passed validation but is not a ticket status.");
        }

        var (ticket, actor) = await scope.ActAsync(command.TenantId, command.TicketId, command.ExpectedVersion, cancellationToken);
        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var now = context.Now;
        var version = ticket.Version;
        var wasFinished = ticket.Status is SupportTicketStatus.Resolved or SupportTicketStatus.Closed;
        ticket.ChangeStatus(status, actor, now, context.Clock);
        // Finishing the work releases the assignee. Published before the save so it travels in the same transaction in both branches.
        if (!wasFinished
            && status is SupportTicketStatus.Resolved or SupportTicketStatus.Closed
            && ticket.AssigneeMemberId is { } assignee)
        {
            var reason = status is SupportTicketStatus.Resolved
                ? SupportAssigneeReleaseReasons.Resolved
                : SupportAssigneeReleaseReasons.Closed;
            integrationEvents.PublishAssigneeReleased(ticket, assignee, reason, now);
        }

        if (status is not SupportTicketStatus.Closed || ticket.ConversationId is not { } conversationId)
        {
            await SupportTicketActions.SaveIfChangedAsync(
                ticket, version, executionContext, auditPublisher, unitOfWork, SupportAuditActions.TicketStatusChanged, ChangedFields, now,
                cancellationToken);
            if (ticket.Version != version)
            {
                await SupportTicketNotices.ChangedAsync(realtime, ticket, formerAssigneeMemberId: null, CancellationToken.None);
            }

            return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
        }

        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            await conversationEvents.AppendAsync(
                command.TenantId, conversationId, opened: false, ticket.Id.Value, actor, now, cancellationToken);
            await SupportTicketActions.SaveIfChangedAsync(
                ticket, version, executionContext, auditPublisher, unitOfWork, SupportAuditActions.TicketStatusChanged, ChangedFields, now,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await conversationEvents.NotifyChangedAsync(command.TenantId, conversationId, cancellationToken);
        if (ticket.Version != version)
        {
            await SupportTicketNotices.ChangedAsync(realtime, ticket, formerAssigneeMemberId: null, CancellationToken.None);
        }

        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }
}
