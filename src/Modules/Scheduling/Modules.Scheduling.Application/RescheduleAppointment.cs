using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

public sealed record RescheduleAppointmentCommand(Guid TenantId, Guid AppointmentId, RescheduleAppointmentRequest Request, string? IfMatch)
    : ICommand<AppointmentDto>;

/// <summary>
/// Cambiar la hora (y, si se pide, el recurso) de una cita que sigue abierta. Conserva el id y el estado, deja una entrada en el historial
/// y vuelve a validar todo como una reserva nueva: el inicio debe ser uno que las reglas ofrezcan (422 <c>outside_rules</c>), sobre recursos
/// de la misma sede de la cita y dentro del alcance de quien la mueve, con el cupo comprobado bajo el candado de los recursos (409). El cupo
/// viejo se libera primero y se guarda antes de tomar el nuevo, para que el rango que la cita deja no estorbe al que ocupa (la restricción
/// de exclusión se evalúa sentencia a sentencia); todo va en una sola transacción, así que un 409 deja la cita donde estaba.
/// </summary>
public sealed class RescheduleAppointmentHandler(
    AppointmentTransitions transitions,
    IServiceRepository services,
    AvailabilityLoader loader,
    ISessionRepository sessions,
    IAppointmentRepository appointments,
    ISchedulingUnitOfWork unitOfWork,
    IValidator<RescheduleAppointmentRequest> validator)
    : ICommandHandler<RescheduleAppointmentCommand, AppointmentDto>
{
    public async Task<AppointmentDto> HandleAsync(RescheduleAppointmentCommand command, CancellationToken cancellationToken)
    {
        var (appointment, scope) = await transitions.LoadAsync(command.TenantId, command.AppointmentId, command.IfMatch, cancellationToken);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        // A closed appointment answers invalid_transition before anything is computed for it.
        if (appointment.Status is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed))
        {
            throw new SchedulingDomainException(
                SchedulingErrorCodes.InvalidTransition, $"An appointment in status '{appointment.Status}' cannot do that.");
        }

        var tenantId = command.TenantId;
        ResourceId? requested = command.Request.ResourceId is { } requestedId ? new ResourceId(requestedId) : null;
        if (requested is { } outsider && !scope.Includes(outsider.Value))
        {
            throw SchedulingAuthorization.Denied();
        }

        var startsAt = command.Request.StartsAt!.Value.ToUniversalTime();
        if (startsAt == appointment.StartsAt && (requested is null || requested == appointment.ResourceId))
        {
            SchedulingFieldErrors.Throw("startsAt", "La cita ya tiene ese horario. Elige otro.");
        }

        var service = (await services.FindManyAsync(tenantId, [appointment.ServiceId], cancellationToken)).SingleOrDefault()
            ?? throw SchedulingNotFound.Service(appointment.ServiceId.Value);
        var loaded = await loader.LoadForStartAsync(tenantId, scope, startsAt, cancellationToken);
        var sameLocation = loaded.OfferedResources.Where(resource => resource.LocationId == appointment.LocationId).Select(resource => resource.Id).ToHashSet();
        var rulesSessions = await loader.SessionsAsync(tenantId, loaded, loaded.OfferedResourceIds, onlyActive: false, cancellationToken);
        var candidates = AppointmentStarts.RulesCandidates(loaded, service, rulesSessions, startsAt, requested, sameLocation.Contains);

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
        var option = AppointmentStarts.PickFree(loaded, service, fresh, startsAt, candidates, preferred: requested ?? appointment.ResourceId);

        var (actor, now) = await transitions.ActorAndNowAsync(tenantId, actsAsClient: false, cancellationToken);
        oldSession.Release(now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var session = await AppointmentStarts.TakeSeatAsync(sessions, tenantId, option, service, startsAt, now, cancellationToken);
        var changedFields = option.ResourceId == appointment.ResourceId ? new[] { "startsAt" } : ["resourceId", "startsAt"];
        appointment.Reschedule(option.ResourceId, session.Id, startsAt, startsAt.AddMinutes(service.DurationMinutes), actor, now);

        var result = await transitions.SaveAsync(
            tenantId, appointment, SchedulingAuditActions.AppointmentRescheduled, changedFields, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
