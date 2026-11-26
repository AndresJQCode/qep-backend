using BuildingBlocks.Application;
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

    /// <summary>
    /// D-15: a client moves an appointment under the same cutoff that rules cancelling it (otherwise moving it would dodge the deadline), and
    /// only when its service is one the channel books; a person moves the rest. Both are invalid_transition, which the agent turns into a handoff.
    /// </summary>
    private static void EnsureClientMayMove(Appointment appointment, Service service, SchedulingSettings rules, DateTimeOffset now)
    {
        if (!service.BookableByChannel)
        {
            throw new SchedulingDomainException(SchedulingErrorCodes.InvalidTransition, AppointmentCancellationRules.NotMovableByChatMessage);
        }

        if (!AppointmentCancellationRules.ClientMayCancel(appointment.StartsAt, now, rules.ClientCancelCutoffHours))
        {
            throw new SchedulingDomainException(
                SchedulingErrorCodes.InvalidTransition, AppointmentCancellationRules.TooLateToMoveMessage(rules.ClientCancelCutoffHours));
        }
    }

    /// <param name="byClient">Rige el plazo del cliente y exige <c>bookableByChannel</c> (D-15): lo pide el agente.</param>
    /// <param name="requireClientChoice">El recurso pedido tiene que ser uno que la franja deja elegir al cliente (D-16).</param>
    /// <returns><c>false</c> when, under the lock, the appointment turned out to be at that start already (somebody moved it there between the
    /// read and the lock): nothing was written. The HTTP handler checks the same thing before calling, so it never sees <c>false</c>.</returns>
    public async Task<bool> RescheduleAsync(
        Guid tenantId,
        Appointment appointment,
        ResourceScope scope,
        ResourceId? resourceId,
        DateTimeOffset startsAt,
        SchedulingActor actor,
        bool byClient,
        bool requireClientChoice,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        EnsureOpen(appointment);
        var readBeforeTheLock = (appointment.ResourceId, appointment.StartsAt, appointment.LocationId);

        var (service, loaded, candidates) = await RulesAsync(
            tenantId, appointment, scope, resourceId, startsAt, byClient, requireClientChoice, cancellationToken);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await appointments.LockResourcesAsync(tenantId, [.. candidates, appointment.ResourceId], cancellationToken);

        // Read before the lock, so read again: a cancel or a move that committed in between is visible only now.
        await appointments.RefreshAsync(appointment, cancellationToken);
        AppointmentCancellation.EnsureNotChanged(appointment, expectedVersion);
        EnsureOpen(appointment);
        if (appointment.StartsAt == startsAt && (resourceId is null || resourceId == appointment.ResourceId))
        {
            return false;
        }

        // The cutoff used the old start, the lock the old resource and the candidates the old location. If another write moved the
        // appointment in between, those decisions are stale: the caller re-reads and decides again.
        if ((appointment.ResourceId, appointment.StartsAt, appointment.LocationId) != readBeforeTheLock)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The appointment changed after it was read.");
        }

        // Same reason as in booking: closures committed while the candidates were computed are visible only after the lock.
        loaded = await loader.LoadForStartAsync(tenantId, scope, startsAt, cancellationToken);
        var (oldSession, fresh) = await SeatsWithoutTheReleasedOneAsync(tenantId, loaded, candidates, appointment, cancellationToken);
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
        return true;
    }

    /// <summary>
    /// Lo que <see cref="RescheduleAsync"/> comprueba sin escribir ni tomar el candado: las mismas reglas y el mismo cupo libre, sin contar el que
    /// la propia cita ocupa (spec 2026-11-23 menores, M1). Es la vista previa del playground; el cupo puede perderse antes del cambio real.
    /// </summary>
    public async Task<(Service Service, LoadedAvailability Loaded, SlotOption Option)> CheckAsync(
        Guid tenantId,
        Appointment appointment,
        ResourceScope scope,
        ResourceId? resourceId,
        DateTimeOffset startsAt,
        bool byClient,
        bool requireClientChoice,
        CancellationToken cancellationToken)
    {
        EnsureOpen(appointment);
        var (service, loaded, candidates) = await RulesAsync(
            tenantId, appointment, scope, resourceId, startsAt, byClient, requireClientChoice, cancellationToken);
        var (_, fresh) = await SeatsWithoutTheReleasedOneAsync(tenantId, loaded, candidates, appointment, cancellationToken);
        var option = AppointmentStarts.PickFree(loaded, service, fresh, startsAt, candidates, preferred: resourceId ?? appointment.ResourceId);
        return (service, loaded, option);
    }

    private async Task<(Service Service, LoadedAvailability Loaded, IReadOnlyList<ResourceId> Candidates)> RulesAsync(
        Guid tenantId,
        Appointment appointment,
        ResourceScope scope,
        ResourceId? resourceId,
        DateTimeOffset startsAt,
        bool byClient,
        bool requireClientChoice,
        CancellationToken cancellationToken)
    {
        var service = (await services.FindManyAsync(tenantId, [appointment.ServiceId], cancellationToken)).SingleOrDefault()
            ?? throw SchedulingNotFound.Service(appointment.ServiceId.Value);
        var loaded = await loader.LoadForStartAsync(tenantId, scope, startsAt, cancellationToken);
        if (byClient)
        {
            EnsureClientMayMove(appointment, service, loaded.Rules, loaded.Calendar.UtcNow);
        }

        var sameLocation = loaded.OfferedResources.Where(resource => resource.LocationId == appointment.LocationId).Select(resource => resource.Id).ToHashSet();
        var rulesSessions = await loader.SessionsAsync(tenantId, loaded, loaded.OfferedResourceIds, onlyActive: false, cancellationToken);
        var candidates = AppointmentStarts.RulesCandidates(loaded, service, rulesSessions, startsAt, resourceId, sameLocation.Contains);
        if (requireClientChoice && resourceId is { } mustBeChoosable)
        {
            AppointmentStarts.EnsureClientChoice(loaded, service, rulesSessions, startsAt, mustBeChoosable);
        }

        return (service, loaded, candidates);
    }

    private async Task<(Session OldSession, IReadOnlyList<Session> Fresh)> SeatsWithoutTheReleasedOneAsync(
        Guid tenantId,
        LoadedAvailability loaded,
        IReadOnlyList<ResourceId> candidates,
        Appointment appointment,
        CancellationToken cancellationToken)
    {
        var oldSession = await sessions.FindAsync(tenantId, appointment.SessionId, cancellationToken)
            ?? throw new InvalidOperationException($"The session of appointment '{appointment.Id.Value}' does not exist.");
        // The seat the appointment holds is about to be released: if it is the only one of an ad-hoc session, that session leaves and
        // its range must not block the new start (it would, for a service with margin, or for a move to the next start).
        var fresh = (await loader.SessionsAsync(tenantId, loaded, candidates, onlyActive: true, cancellationToken))
            .Where(session => !(session.Id == oldSession.Id && session.BookedCount == 1 && !session.IsPublished))
            .ToList();
        return (oldSession, fresh);
    }
}
