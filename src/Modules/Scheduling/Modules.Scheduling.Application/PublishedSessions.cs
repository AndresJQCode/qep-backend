using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

public sealed record PublishSessionCommand(Guid TenantId, PublishSessionRequest Request) : ICommand<PublishedSessionDto>;

public sealed record RemovePublishedSessionCommand(Guid TenantId, Guid SessionId) : ICommand<PublishedSessionRemovedResult>;

/// <summary>El resultado de quitar una sesión publicada: la ruta responde 204 sin cuerpo.</summary>
public sealed record PublishedSessionRemovedResult;

/// <summary>
/// Publica una sesión de una franja con cupo mayor que 1 y modo <c>PublishedSessions</c>: sólo ésas se reservan en esa franja. La sesión
/// nace con el cupo de la franja, para un recurso y un servicio que ella declara y con un inicio dentro de una de sus ventanas del día
/// (en la zona horaria del tenant, sin festivos si la franja los cierra). El recurso es de su alcance o responde 403. Si otra sesión
/// activa del recurso ocupa ese tiempo, la restricción de exclusión de la base lo rechaza con 409 <c>slot_taken</c> (Unit of Work).
/// </summary>
public sealed class PublishSessionHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    SchedulingFirstAccess firstAccess,
    ResourceScopeResolver scopeResolver,
    IAvailabilityBlockRepository blocks,
    IResourceRepository resources,
    IServiceRepository services,
    ISessionRepository sessions,
    ISchedulingHolidayCalendar holidays,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork,
    IValidator<PublishSessionRequest> validator)
    : ICommandHandler<PublishSessionCommand, PublishedSessionDto>
{
    public async Task<PublishedSessionDto> HandleAsync(PublishSessionCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AvailabilityManage);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);
        await firstAccess.EnsureAsync(command.TenantId, cancellationToken);

        var request = command.Request;
        var resourceId = new ResourceId(request.ResourceId!.Value);
        var serviceId = new ServiceId(request.ServiceId!.Value);
        if (!(await scopeResolver.ResolveAsync(command.TenantId, cancellationToken)).Includes(resourceId.Value))
        {
            throw SchedulingAuthorization.Denied();
        }

        var block = await blocks.FindAsync(command.TenantId, new AvailabilityBlockId(request.BlockId!.Value), cancellationToken);
        if (block is null || block.Archived)
        {
            SchedulingFieldErrors.Throw("blockId", "La franja no existe o está archivada.");
        }

        if (block.SessionMode != SessionMode.PublishedSessions)
        {
            throw new SchedulingDomainException(
                SchedulingErrorCodes.SessionNotPublishedMode,
                "Esa franja no publica sesiones: sólo las de cupo mayor que 1 con el modo de sesiones publicadas.");
        }

        if (!block.ResourceIds.Contains(resourceId))
        {
            SchedulingFieldErrors.Throw("resourceId", "Ese recurso no atiende esta franja.");
        }

        if (!block.ServiceIds.Contains(serviceId))
        {
            SchedulingFieldErrors.Throw("serviceId", "Ese servicio no se reserva en esta franja.");
        }

        var resource = (await resources.FindManyAsync(command.TenantId, [resourceId], cancellationToken)).SingleOrDefault();
        if (resource is null || resource.Archived)
        {
            SchedulingFieldErrors.Throw("resourceId", "El recurso no existe en esta organización o está archivado.");
        }

        var service = (await services.FindManyAsync(command.TenantId, [serviceId], cancellationToken)).SingleOrDefault();
        if (service is null || service.Archived)
        {
            SchedulingFieldErrors.Throw("serviceId", "El servicio no existe en esta organización o está archivado.");
        }

        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var startsAt = request.StartsAt!.Value.ToUniversalTime();
        var endsAt = startsAt.AddMinutes(service.DurationMinutes);
        if (!await FitsAWindowAsync(command.TenantId, block, calendar.TimeZone, startsAt, endsAt, cancellationToken))
        {
            SchedulingFieldErrors.Throw("startsAt", "La sesión debe caber entera en un horario abierto de la franja.");
        }

        // The table key is (resource, service, start): an inactive row (a removed session that kept its cancelled history) is brought back
        // instead of inserting a second one; an active row is a real conflict.
        var session = await sessions.FindByKeyAsync(command.TenantId, resourceId, serviceId, startsAt, cancellationToken);
        string action;
        IReadOnlyCollection<string> auditedFields = ["blockId", "resourceId", "serviceId", "startsAt"];
        if (session is null)
        {
            session = Session.Create(
                SessionId.New(), command.TenantId, resourceId, serviceId, startsAt, endsAt, service.BufferMinutes,
                block.CapacityPerResource, isPublished: true, calendar.UtcNow);
            sessions.Add(session);
            action = SchedulingAuditActions.SessionPublished;
        }
        else if (session.IsActive)
        {
            throw new ResourceConflictException(SchedulingErrorCodes.SlotTaken, "Ese espacio ya no está disponible. Elige otro.");
        }
        else
        {
            // A reactivation always changes isActive, then lists what the row took from the current service and block (not the key it
            // already had): the entry is never empty, even when the end and the capacity are the same as before.
            var reactivated = new List<string> { "isActive" };
            if (session.EndsAt != endsAt || session.OccupiesUntil != endsAt.AddMinutes(service.BufferMinutes))
            {
                reactivated.Add("endsAt");
            }

            if (session.Capacity != block.CapacityPerResource)
            {
                reactivated.Add("capacity");
            }

            auditedFields = reactivated;
            session.Republish(endsAt, service.BufferMinutes, block.CapacityPerResource, calendar.UtcNow);
            action = SchedulingAuditActions.SessionReactivated;
        }

        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, action,
            SchedulingAuditActions.Resources.Session, session.Id.Value, auditedFields, calendar.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return SchedulingComposer.PublishedSession(session);
    }

    private async Task<bool> FitsAWindowAsync(
        Guid tenantId,
        AvailabilityBlock block,
        TimeZoneInfo timeZone,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        CancellationToken cancellationToken)
    {
        // La franja se expande por el día del calendario del tenant, no por el día UTC.
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(startsAt, timeZone).DateTime);
        if (block.ClosesOnHolidays && (await holidays.HolidaysAsync(tenantId, day, day, cancellationToken)).Contains(day))
        {
            return false;
        }

        return block.RangesOn(day).Any(range =>
            AvailabilityEngine.ToInstant(day, range.From, timeZone) <= startsAt
            && endsAt <= AvailabilityEngine.ToInstant(day, range.To, timeZone));
    }
}

/// <summary>
/// Quita una sesión publicada. Una sesión con alguna cita que no esté cancelada no se quita (422 <c>has_appointments</c>): el cupo
/// reservado es de alguien. Sin citas se borra la fila; con sólo citas canceladas se desactiva (<c>is_active = false</c>), porque la
/// llave foránea del historial impide borrarla, y deja de ocupar al recurso. Publicarla de nuevo la reactiva.
/// </summary>
public sealed class RemovePublishedSessionHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ResourceScopeResolver scopeResolver,
    ISessionRepository sessions,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork)
    : ICommandHandler<RemovePublishedSessionCommand, PublishedSessionRemovedResult>
{
    public async Task<PublishedSessionRemovedResult> HandleAsync(RemovePublishedSessionCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AvailabilityManage);

        // Una sesión que el tenant no publicó (la abrió una reserva) no se quita por esta ruta: para ella es como si no existiera.
        var session = await sessions.FindAsync(command.TenantId, new SessionId(command.SessionId), cancellationToken);
        // A removed (inactive) published session also answers 404: removing it twice is not an error the tenant can act on.
        if (session is null || !session.IsPublished || !session.IsActive)
        {
            throw SchedulingNotFound.Session(command.SessionId);
        }

        if (!(await scopeResolver.ResolveAsync(command.TenantId, cancellationToken)).Includes(session.ResourceId.Value))
        {
            throw SchedulingAuthorization.Denied();
        }

        if (session.BookedCount > 0 || await sessions.HasLiveAppointmentsAsync(command.TenantId, session.Id, cancellationToken))
        {
            throw new SchedulingDomainException(
                SchedulingErrorCodes.SessionHasAppointments,
                "Esa sesión tiene citas. Reprográmalas o cancélalas antes de quitarla.");
        }

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        string action;
        if (await sessions.HasAppointmentsAsync(command.TenantId, session.Id, cancellationToken))
        {
            session.Deactivate(now);
            action = SchedulingAuditActions.SessionDeactivated;
        }
        else
        {
            sessions.Remove(session);
            action = SchedulingAuditActions.SessionRemoved;
        }

        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, action,
            SchedulingAuditActions.Resources.Session, session.Id.Value, ["removed"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new PublishedSessionRemovedResult();
    }
}
