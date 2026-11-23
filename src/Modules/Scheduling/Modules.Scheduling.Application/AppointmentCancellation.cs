using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

/// <summary>
/// El núcleo de la cancelación (spec 2026-11-23 §6.2), sin autorización: lo usan <c>CancelAppointmentHandler</c> (después de cargar la cita con
/// su <c>If-Match</c> y su alcance) y <c>IChannelScheduling</c>. Aplica el plazo del cliente (<paramref name="byClient"/>), toma el candado del
/// recurso de la cita, libera el cupo de su sesión y guarda cambio, historial y auditoría en una sola transacción. Bajo el mismo candado que la
/// reserva, una cancelación y una reserva del mismo recurso se serializan; cancelar la última reserva de una sesión abierta por una reserva libre
/// libera el rango (la sesión se desactiva).
/// </summary>
public sealed class AppointmentCancellation(
    ITenantClock tenantClock,
    ISchedulingSettingsRepository settings,
    ISessionRepository sessions,
    IAppointmentRepository appointments,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork)
{
    public async Task CancelAsync(
        Guid tenantId, Appointment appointment, string? reason, bool byClient, SchedulingActor actor, CancellationToken cancellationToken)
    {
        var now = (await tenantClock.GetAsync(tenantId, cancellationToken)).UtcNow;

        // The cutoff only matters for an appointment that can still be cancelled: a closed one answers invalid_transition on its own.
        if (byClient && appointment.Status is AppointmentStatus.Pending or AppointmentStatus.Confirmed)
        {
            var rules = await settings.FindAsync(tenantId, cancellationToken)
                ?? throw new InvalidOperationException("The scheduling rules row is missing: first access provisions it before any write.");
            if (!AppointmentCancellationRules.ClientMayCancel(appointment.StartsAt, now, rules.ClientCancelCutoffHours))
            {
                throw new SchedulingDomainException(
                    SchedulingErrorCodes.InvalidTransition, AppointmentCancellationRules.TooLateMessage(rules.ClientCancelCutoffHours));
            }
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await appointments.LockResourcesAsync(tenantId, [appointment.ResourceId], cancellationToken);

        // The appointment was read before the lock: whoever held it may have cancelled or moved it. Read it again so a closed one answers
        // invalid_transition below instead of releasing a seat that is already free.
        await appointments.RefreshAsync(appointment, cancellationToken);

        var session = await sessions.FindAsync(tenantId, appointment.SessionId, cancellationToken)
            ?? throw new InvalidOperationException($"The session of appointment '{appointment.Id.Value}' does not exist.");
        // The domain rejects an appointment that is already closed before the seat is released.
        appointment.Cancel(reason, actor.HistoryMemberId, now, actor.ByChannel);
        session.Release(now);

        SchedulingAudit.Publish(
            auditPublisher, actor, tenantId, SchedulingAuditActions.AppointmentCancelled, SchedulingAuditActions.Resources.Appointment,
            appointment.Id.Value, string.IsNullOrWhiteSpace(reason) ? ["status"] : ["status", "cancelReason"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
