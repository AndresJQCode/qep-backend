using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

public sealed record ChangeTicketTypeRequest(Guid? TicketTypeId);

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record ChangeTicketTypeCommand(Guid TenantId, Guid TicketId, Guid TicketTypeId, long ExpectedVersion)
    : ICommand<SupportTicketDetailDto>
{
    public static ChangeTicketTypeCommand From(Guid tenantId, Guid ticketId, ChangeTicketTypeRequest request, long expectedVersion) =>
        new(tenantId, ticketId, request.TicketTypeId ?? Guid.Empty, expectedVersion);
}

public sealed class ChangeTicketTypeValidator : AbstractValidator<ChangeTicketTypeCommand>
{
    public ChangeTicketTypeValidator()
    {
        RuleFor(command => command.TicketTypeId).NotEqual(Guid.Empty);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>Cambia el tipo por uno activo del tenant y recalcula el vencimiento desde la radicación con el término nuevo más las
/// pausas ya contadas (spec §7.2). Se permite en cualquier estado (pre-flight R9).</summary>
public sealed class ChangeTicketTypeHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    ISupportTicketTypeRepository types,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer,
    IValidator<ChangeTicketTypeCommand> validator)
    : ICommandHandler<ChangeTicketTypeCommand, SupportTicketDetailDto>
{
    private static readonly string[] ChangedFields = ["ticketTypeId"];

    public async Task<SupportTicketDetailDto> HandleAsync(ChangeTicketTypeCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var (ticket, actor) = await scope.ManageAsync(command.TenantId, command.TicketId, cancellationToken);
        SupportTicketScope.EnsureVersion(ticket, command.ExpectedVersion);
        // Acotado al tenant: así el dominio nunca recibe un tipo de otro tenant (su guarda lanzaría ArgumentException).
        var type = await SupportTicketRules.EnsureTypeAsync(types, command.TenantId, command.TicketTypeId, cancellationToken);
        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var now = context.Now;
        var version = ticket.Version;
        ticket.ChangeType(type, actor, now, context.Clock);
        await SupportTicketActions.SaveIfChangedAsync(
            ticket, version, executionContext, auditPublisher, unitOfWork, SupportAuditActions.TicketTypeChanged, ChangedFields, now,
            cancellationToken);
        if (ticket.Version != version)
        {
            await realtime.TicketChangedAsync(command.TenantId, ticket.Id.Value, CancellationToken.None);
        }

        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }
}
