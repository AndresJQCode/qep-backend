using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

public sealed record CancelAppointmentCommand(Guid TenantId, Guid AppointmentId, CancelAppointmentRequest? Request, string? IfMatch)
    : ICommand<AppointmentDto>;

/// <summary>
/// The cutoff of a client's own cancellation (spec 2026-11-21, «Citas»): through a channel the client cancels until
/// <c>client_cancel_cutoff_hours</c> before the start; the back office has no limit. Pure so it is tested without a database.
/// </summary>
public static class AppointmentCancellationRules
{
    public static bool ClientMayCancel(DateTimeOffset startsAt, DateTimeOffset now, int cutoffHours) =>
        startsAt - now >= TimeSpan.FromHours(cutoffHours);

    public static string TooLateMessage(int cutoffHours) =>
        $"Ya no puedes cancelar esta cita por tu cuenta: se puede hasta {cutoffHours} horas antes. Comunícate con nosotros.";
}

/// <summary>
/// Cancelar una cita. Libera el cupo de su sesión en la misma transacción y bajo el mismo candado de recurso que la reserva, así que una
/// cancelación y una reserva del mismo recurso se serializan; cancelar la última reserva de una sesión abierta por una reserva libera el
/// rango (la sesión se desactiva). Un cliente por canal no cancela dentro del plazo de las reglas; el back office sí.
/// </summary>
public sealed class CancelAppointmentHandler(
    AppointmentTransitions transitions,
    ISchedulingSettingsRepository settings,
    ISessionRepository sessions,
    IAppointmentRepository appointments,
    ISchedulingUnitOfWork unitOfWork,
    IValidator<CancelAppointmentRequest> validator)
    : ICommandHandler<CancelAppointmentCommand, AppointmentDto>
{
    public async Task<AppointmentDto> HandleAsync(CancelAppointmentCommand command, CancellationToken cancellationToken)
    {
        var (appointment, _) = await transitions.LoadAsync(command.TenantId, command.AppointmentId, command.IfMatch, cancellationToken);
        var request = command.Request ?? new CancelAppointmentRequest(null, null);
        await validator.ValidateAndThrowAsync(request, cancellationToken);

        var byClient = SchedulingEnums.TryParse<AppointmentSource>(request.Source, out var source) && source == AppointmentSource.Channel;
        var (actor, now) = await transitions.ActorAndNowAsync(command.TenantId, byClient, cancellationToken);

        // The cutoff only matters for an appointment that can still be cancelled: a closed one answers invalid_transition on its own.
        if (byClient && appointment.Status is AppointmentStatus.Pending or AppointmentStatus.Confirmed)
        {
            var rules = await settings.FindAsync(command.TenantId, cancellationToken)
                ?? throw new InvalidOperationException("The scheduling rules row is missing: first access provisions it before any write.");
            if (!AppointmentCancellationRules.ClientMayCancel(appointment.StartsAt, now, rules.ClientCancelCutoffHours))
            {
                throw new SchedulingDomainException(
                    SchedulingErrorCodes.InvalidTransition, AppointmentCancellationRules.TooLateMessage(rules.ClientCancelCutoffHours));
            }
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await appointments.LockResourcesAsync(command.TenantId, [appointment.ResourceId], cancellationToken);

        var session = await sessions.FindAsync(command.TenantId, appointment.SessionId, cancellationToken)
            ?? throw new InvalidOperationException($"The session of appointment '{appointment.Id.Value}' does not exist.");
        // The domain rejects an appointment that is already closed before the seat is released.
        appointment.Cancel(request.Reason, actor, now);
        session.Release(now);

        var result = await transitions.SaveAsync(
            command.TenantId, appointment, SchedulingAuditActions.AppointmentCancelled,
            string.IsNullOrWhiteSpace(request.Reason) ? ["status"] : ["status", "cancelReason"], now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
