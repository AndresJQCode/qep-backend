using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

/// <summary>
/// El núcleo del cambio de hora (spec 2026-11-23 §6.2), sin autorización: lo usan <c>RescheduleAppointmentHandler</c> y
/// <c>IChannelScheduling</c>. Conserva el id y el estado, deja una entrada en el historial y vuelve a validar todo como una reserva nueva: el
/// inicio debe ser uno que las reglas ofrezcan (422 <c>outside_rules</c>), sobre recursos de la misma sede de la cita y dentro del alcance, con
/// el cupo comprobado bajo el candado de los recursos (409). El cupo viejo se libera primero y se guarda antes de tomar el nuevo, para que el
/// rango que la cita deja no estorbe al que ocupa (la restricción de exclusión se evalúa sentencia a sentencia); todo va en una sola
/// transacción, así que un 409 deja la cita donde estaba.
/// </summary>
public sealed class AppointmentRescheduling(
    ITenantClock tenantClock,
    IServiceRepository services,
    AvailabilityLoader loader,
    ISessionRepository sessions,
    IAppointmentRepository appointments,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork)
{
    /// <summary>A closed appointment answers invalid_transition before anything is computed for it.</summary>
    public static void EnsureOpen(Appointment appointment)
    {
        if (appointment.Status is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed))
        {
            throw new SchedulingDomainException(
                SchedulingErrorCodes.InvalidTransition, $"An appointment in status '{appointment.Status}' cannot do that.");
        }
    }

    /// <param name="byClient">Rige el plazo del cliente y exige <c>bookableByChannel</c> (D-15): lo pide el agente.</param>
    /// <param name="requireClientChoice">El recurso pedido tiene que ser uno que la franja deja elegir al cliente (D-16).</param>
    public async Task RescheduleAsync(
        Guid tenantId,
        Appointment appointment,
        ResourceScope scope,
        ResourceId? resourceId,
        DateTimeOffset startsAt,
        SchedulingActor actor,
        bool byClient,
        bool requireClientChoice,
        CancellationToken cancellationToken)
    {
        EnsureOpen(appointment);

        var service = (await services.FindManyAsync(tenantId, [appointment.ServiceId], cancellationToken)).SingleOrDefault()
            ?? throw SchedulingNotFound.Service(appointment.ServiceId.Value);
        var loaded = await loader.LoadForStartAsync(tenantId, scope, startsAt, cancellationToken);
        var sameLocation = loaded.OfferedResources.Where(resource => resource.LocationId == appointment.LocationId).Select(resource => resource.Id).ToHashSet();
        var rulesSessions = await loader.SessionsAsync(tenantId, loaded, loaded.OfferedResourceIds, onlyActive: false, cancellationToken);
        var candidates = AppointmentStarts.RulesCandidates(loaded, service, rulesSessions, startsAt, resourceId, sameLocation.Contains);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await appointments.LockResourcesAsync(tenantId, [.. candidates, appointment.ResourceId], cancellationToken);

        // Same reason as in booking: closures committed while the candidates were computed are visible only after the lock.
        loaded = await loader.LoadForStartAsync(tenantId, scope, startsAt, cancellationToken);
        var oldSession = await sessions.FindAsync(tenantId, appointment.SessionId, cancellationToken)
            ?? throw new InvalidOperationException($"The session of appointment '{appointment.Id.Value}' does not exist.");
        // The seat the appointment holds is about to be released: if it is the only one of an ad-hoc session, that session leaves and
        // its range must not block the new start (it would, for a service with margin, or for a move to the next start).
        var fresh = (await loader.SessionsAsync(tenantId, loaded, candidates, onlyActive: true, cancellationToken))
            .Where(session => !(session.Id == oldSession.Id && session.BookedCount == 1 && !session.IsPublished))
            .ToList();
        var option = AppointmentStarts.PickFree(loaded, service, fresh, startsAt, candidates, preferred: resourceId ?? appointment.ResourceId);

        var now = (await tenantClock.GetAsync(tenantId, cancellationToken)).UtcNow;
        oldSession.Release(now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var session = await AppointmentStarts.TakeSeatAsync(sessions, tenantId, option, service, startsAt, now, cancellationToken);
        var changedFields = option.ResourceId == appointment.ResourceId ? new[] { "startsAt" } : ["resourceId", "startsAt"];
        appointment.Reschedule(
            option.ResourceId, session.Id, startsAt, startsAt.AddMinutes(service.DurationMinutes), actor.HistoryMemberId, now, actor.ByChannel);

        SchedulingAudit.Publish(
            auditPublisher, actor, tenantId, SchedulingAuditActions.AppointmentRescheduled, SchedulingAuditActions.Resources.Appointment,
            appointment.Id.Value, changedFields, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
