using BuildingBlocks.Application;
using FluentValidation;
using Modules.Coordinadora.Domain;
using Modules.Tenancy.Application;

namespace Modules.Coordinadora.Application.Shipments;

/// <summary><c>POST …/orders/{orderId}/coordinadora-shipment/track</c>: <c>Guias_rastreoSimple</c> de la guía viva.</summary>
public sealed record TrackCoordinadoraShipmentCommand(Guid TenantId, Guid OrderId) : ICommand<CoordinadoraShipmentDto>;

/// <summary>Spec 2026-11-21 §5.5: guarda el estado que dio Coordinadora y devuelve la fila. Sólo una <c>Created</c>. Sin worker: a pedido de la pantalla.</summary>
public sealed class TrackCoordinadoraShipmentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ICoordinadoraShipmentRepository shipments,
    ICoordinadoraConnectionDirectory connections,
    ICoordinadoraApi api,
    ICoordinadoraUnitOfWork unitOfWork,
    IClock clock) : ICommandHandler<TrackCoordinadoraShipmentCommand, CoordinadoraShipmentDto>
{
    public async Task<CoordinadoraShipmentDto> HandleAsync(TrackCoordinadoraShipmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await CoordinadoraShipmentSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        var rows = await shipments.ListByOrderAsync(command.TenantId, command.OrderId, cancellationToken);
        var shipment = CoordinadoraShipmentSupport.LiveOrThrow(rows, command.OrderId);
        shipment.EnsureCreated("Only a guide created in Coordinadora can be tracked.");

        var credentials = await CoordinadoraShipmentSupport.CredentialsAsync(connections, command.TenantId, shipment.ConnectionId, cancellationToken);
        var tracking = await api.TrackAsync(credentials, shipment.GuideNumber!, cancellationToken);
        shipment.RecordTracking(tracking.Status, tracking.Detail, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return CoordinadoraShipmentDto.From(shipment);
    }
}

/// <summary><c>POST …/orders/{orderId}/coordinadora-shipment/cancel</c>: <c>Guias_anularGuia</c> de la guía viva.</summary>
public sealed record CancelCoordinadoraShipmentCommand(Guid TenantId, Guid OrderId) : ICommand<CoordinadoraShipmentDto>;

/// <summary>Spec 2026-11-21 §5.5: la fila pasa a <c>Cancelled</c> y el pedido queda libre para otra guía. Un rechazo de Coordinadora es 422
/// <c>cancel_rejected</c> y la fila no cambia; sin respuesta, 503 y tampoco.</summary>
public sealed class CancelCoordinadoraShipmentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory memberships,
    ICoordinadoraShipmentRepository shipments,
    ICoordinadoraConnectionDirectory connections,
    ICoordinadoraApi api,
    ICoordinadoraUnitOfWork unitOfWork,
    ICoordinadoraAuditRecorder auditRecorder,
    IClock clock) : ICommandHandler<CancelCoordinadoraShipmentCommand, CoordinadoraShipmentDto>
{
    public async Task<CoordinadoraShipmentDto> HandleAsync(CancelCoordinadoraShipmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await CoordinadoraShipmentSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        var memberId = await memberships.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw CoordinadoraAuthorization.Denied();
        var rows = await shipments.ListByOrderAsync(command.TenantId, command.OrderId, cancellationToken);
        var shipment = CoordinadoraShipmentSupport.LiveOrThrow(rows, command.OrderId);
        shipment.EnsureCreated("Only a guide created in Coordinadora can be cancelled.");

        var credentials = await CoordinadoraShipmentSupport.CredentialsAsync(connections, command.TenantId, shipment.ConnectionId, cancellationToken);
        var outcome = await api.CancelGuideAsync(credentials, shipment.GuideNumber!, cancellationToken);
        switch (outcome)
        {
            case CoordinadoraCancelOutcome.Cancelled:
                var now = clock.UtcNow;
                shipment.MarkCancelled(memberId, now);
                CoordinadoraShipmentSupport.Audit(auditRecorder, executionContext, shipment, CoordinadoraAuditActions.ShipmentCancelled, now);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return CoordinadoraShipmentDto.From(shipment);
            case CoordinadoraCancelOutcome.Rejected rejected:
                throw new CoordinadoraDomainException(
                    CoordinadoraErrorCodes.ShipmentCancelRejected,
                    $"Coordinadora did not accept cancelling the guide ({rejected.Code}): {rejected.Message}");
            case CoordinadoraCancelOutcome.Transient transient:
                throw new ServiceUnavailableException(
                    CoordinadoraErrorCodes.Unavailable, $"Coordinadora did not answer the cancellation ({transient.Reason}).");
            default:
                throw new InvalidOperationException("Unknown cancel outcome.");
        }
    }
}

/// <summary><c>POST …/orders/{orderId}/coordinadora-shipment/resolve</c>: la persona miró en Coordinadora. <paramref name="Outcome"/> es
/// <c>exists</c> (con <paramref name="GuideNumber"/>) o <c>missing</c>.</summary>
public sealed record ResolveCoordinadoraShipmentCommand(Guid TenantId, Guid OrderId, string? Outcome, string? GuideNumber)
    : ICommand<CoordinadoraShipmentDto>;

public sealed class ResolveCoordinadoraShipmentValidator : AbstractValidator<ResolveCoordinadoraShipmentCommand>
{
    public const string Exists = "exists";
    public const string Missing = "missing";

    public ResolveCoordinadoraShipmentValidator()
    {
        RuleFor(command => command.Outcome)
            .NotEmpty()
            .Must(value => value is Exists or Missing)
            .WithMessage("'outcome' must be 'exists' or 'missing'.")
            .OverridePropertyName("outcome");
        RuleFor(command => command.GuideNumber)
            .NotEmpty()
            .MaximumLength(CoordinadoraShipment.GuideNumberMaxLength)
            .Matches("^[0-9A-Za-z-]+$")
            .When(command => command.Outcome == Exists)
            .OverridePropertyName("guideNumber");
    }
}

/// <summary>Spec 2026-11-21 §5.5: sólo desde <c>Unknown</c> (422 <c>state_invalid</c> si no). <c>exists</c> deja la guía <c>Created</c> con ese
/// número (sin rótulo guardado: se pide en vivo); <c>missing</c> la pasa a <c>Failed</c> y libera el pedido. Auditoría con quién lo resolvió.</summary>
public sealed class ResolveCoordinadoraShipmentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ICoordinadoraShipmentRepository shipments,
    ICoordinadoraUnitOfWork unitOfWork,
    ICoordinadoraAuditRecorder auditRecorder,
    IClock clock,
    IValidator<ResolveCoordinadoraShipmentCommand> validator) : ICommandHandler<ResolveCoordinadoraShipmentCommand, CoordinadoraShipmentDto>
{
    public async Task<CoordinadoraShipmentDto> HandleAsync(ResolveCoordinadoraShipmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await CoordinadoraShipmentSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var now = clock.UtcNow;
        var rows = await shipments.ListByOrderAsync(command.TenantId, command.OrderId, cancellationToken);
        CoordinadoraShipmentSupport.ExpireStale(rows, now);
        var shipment = CoordinadoraShipmentSupport.LiveOrThrow(rows, command.OrderId);

        if (command.Outcome == ResolveCoordinadoraShipmentValidator.Exists)
        {
            shipment.ResolveExists(command.GuideNumber!.Trim(), now);
        }
        else
        {
            shipment.ResolveMissing(now);
        }

        CoordinadoraShipmentSupport.Audit(
            auditRecorder, executionContext, shipment, CoordinadoraAuditActions.ShipmentResolved, now, command.Outcome);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return CoordinadoraShipmentDto.From(shipment);
    }
}
