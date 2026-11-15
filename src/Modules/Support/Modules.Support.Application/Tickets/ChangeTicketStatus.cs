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
/// El dominio detiene y reanuda el reloj, y cuenta los ciclos de resolución.</summary>
public sealed class ChangeTicketStatusHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IMembershipDirectory membershipDirectory,
    ISupportTicketRepository tickets,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    SupportTicketDtoComposer composer,
    IValidator<ChangeTicketStatusCommand> validator)
    : ICommandHandler<ChangeTicketStatusCommand, SupportTicketDetailDto>
{
    private static readonly string[] ChangedFields = ["status"];

    public async Task<SupportTicketDetailDto> HandleAsync(ChangeTicketStatusCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        if (!SupportTicketRules.TryParseExact<SupportTicketStatus>(command.Status, out var status))
        {
            throw new InvalidOperationException($"Status '{command.Status}' passed validation but is not a ticket status.");
        }

        var (ticket, actor) = await SupportTicketActions.LoadAsync(
            executionContext, membershipDirectory, tickets, command.TenantId, command.TicketId, command.ExpectedVersion, cancellationToken);
        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var version = ticket.Version;
        ticket.ChangeStatus(status, actor, now, context.Clock);
        await SupportTicketActions.SaveIfChangedAsync(
            ticket, version, executionContext, auditPublisher, unitOfWork, SupportAuditActions.TicketStatusChanged, ChangedFields, now,
            cancellationToken);

        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }
}
