using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

public sealed record ListClosuresQuery(Guid TenantId) : IQuery<IReadOnlyList<ClosureDto>>;

public sealed record CreateClosureCommand(Guid TenantId, ClosureRequest Request) : ICommand<ClosureDto>;

public sealed record DeleteClosureCommand(Guid TenantId, Guid ClosureId) : ICommand<ClosureDeletedResult>;

/// <summary>El resultado de borrar un bloqueo: la ruta responde 204 sin cuerpo.</summary>
public sealed record ClosureDeletedResult;

/// <summary>Lo que el bloqueo de una sede entera exige y el de un recurso no: ver todas las agendas.</summary>
internal static class ClosureScope
{
    /// <summary>Un bloqueo de recurso es del alcance si el recurso lo es; uno de toda la sede cierra la agenda de todos y sólo lo toca quien las ve todas.</summary>
    public static bool Allows(ResourceScope scope, ResourceId? resourceId) =>
        resourceId is { } resource ? scope.Includes(resource.Value) : scope.IsAll;
}

/// <summary>Los bloqueos que aún no terminan: el calendario dibuja los de la pantalla (Tarea 10), esta lista es la de administración.</summary>
public sealed class ListClosuresHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    SchedulingFirstAccess firstAccess,
    ResourceScopeResolver scopeResolver,
    IClosureRepository closures)
    : IQueryHandler<ListClosuresQuery, IReadOnlyList<ClosureDto>>
{
    public async Task<IReadOnlyList<ClosureDto>> HandleAsync(ListClosuresQuery query, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AvailabilityManage);
        await firstAccess.EnsureAsync(query.TenantId, cancellationToken);

        var now = (await tenantClock.GetAsync(query.TenantId, cancellationToken)).UtcNow;
        var scope = await scopeResolver.ResolveAsync(query.TenantId, cancellationToken);
        return [.. (await closures.ListAsync(query.TenantId, scope, now, cancellationToken)).Select(SchedulingComposer.Closure)];
    }
}

public sealed class CreateClosureHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    SchedulingFirstAccess firstAccess,
    ResourceScopeResolver scopeResolver,
    ILocationRepository locations,
    IResourceRepository resources,
    IClosureRepository closures,
    IAppointmentRepository appointments,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork,
    IValidator<ClosureRequest> validator)
    : ICommandHandler<CreateClosureCommand, ClosureDto>
{
    public async Task<ClosureDto> HandleAsync(CreateClosureCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AvailabilityManage);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);
        await firstAccess.EnsureAsync(command.TenantId, cancellationToken);

        var request = command.Request;
        ResourceId? resourceId = request.ResourceId is { } id ? new ResourceId(id) : null;
        // El alcance antes que la existencia: quien no ve todas las agendas recibe 403 por un recurso ajeno, no un 422 que diga si existe.
        if (!ClosureScope.Allows(await scopeResolver.ResolveAsync(command.TenantId, cancellationToken), resourceId))
        {
            throw SchedulingAuthorization.Denied();
        }

        var locationId = await ResolveLocationAsync(command.TenantId, request, resourceId, cancellationToken);

        // Npgsql sólo escribe instantes con offset cero.
        var startsAt = request.StartsAt!.Value.ToUniversalTime();
        var endsAt = request.EndsAt!.Value.ToUniversalTime();

        // Same lock bookings take (in id order), held until the commit: a booking that is already inside finishes first and is seen by the
        // check below, one that arrives later re-reads the closures once it holds the lock and finds this one. A closure of a whole
        // location locks every resource of that location, archived ones included (their appointments still count).
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var lockIds = resourceId is { } single
            ? new[] { single }
            : (await resources.ListAsync(command.TenantId, ResourceScope.All, cancellationToken))
                .Where(resource => resource.LocationId == locationId)
                .Select(resource => resource.Id)
                .ToArray();
        await appointments.LockResourcesAsync(command.TenantId, lockIds, cancellationToken);

        if (await closures.HasActiveAppointmentsAsync(command.TenantId, locationId, resourceId, startsAt, endsAt, cancellationToken))
        {
            // Nada se mueve en silencio (spec D-12): quien bloquea decide qué hacer con las citas antes.
            throw new SchedulingDomainException(
                SchedulingErrorCodes.ClosureOverlapsAppointments,
                "Ese bloqueo cruza citas vigentes. Reprográmalas o cancélalas antes de bloquear.");
        }

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var closure = Closure.Create(ClosureId.New(), command.TenantId, locationId, resourceId, startsAt, endsAt, request.Reason, now);
        closures.Add(closure);
        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.ClosureCreated,
            SchedulingAuditActions.Resources.Closure, closure.Id.Value, ["resourceId", "startsAt", "endsAt", "reason"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return SchedulingComposer.Closure(closure);
    }

    private async Task<LocationId> ResolveLocationAsync(
        Guid tenantId, ClosureRequest request, ResourceId? resourceId, CancellationToken cancellationToken)
    {
        if (resourceId is { } resourceKey)
        {
            var resource = (await resources.FindManyAsync(tenantId, [resourceKey], cancellationToken)).SingleOrDefault();
            if (resource is null || resource.Archived)
            {
                SchedulingFieldErrors.Throw("resourceId", "El recurso no existe en esta organización o está archivado.");
            }

            if (request.LocationId is { } given && given != resource.LocationId.Value)
            {
                SchedulingFieldErrors.Throw("locationId", "El recurso no es de esa sede.");
            }

            return resource.LocationId;
        }

        // Sin recurso, la sede entera; sin sede, la principal (un consultorio, una sede: la pantalla no pide un id que el servidor conoce).
        var location = request.LocationId is { } locationId
            ? await locations.FindAsync(tenantId, new LocationId(locationId), cancellationToken)
            : await locations.FindMainAsync(tenantId, cancellationToken);
        if (location is null || location.Archived)
        {
            SchedulingFieldErrors.Throw("locationId", "La sede no existe o está archivada.");
        }

        return location.Id;
    }
}

public sealed class DeleteClosureHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ResourceScopeResolver scopeResolver,
    IClosureRepository closures,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork)
    : ICommandHandler<DeleteClosureCommand, ClosureDeletedResult>
{
    public async Task<ClosureDeletedResult> HandleAsync(DeleteClosureCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AvailabilityManage);

        var closure = await closures.FindAsync(command.TenantId, new ClosureId(command.ClosureId), cancellationToken)
            ?? throw SchedulingNotFound.Closure(command.ClosureId);
        if (!ClosureScope.Allows(await scopeResolver.ResolveAsync(command.TenantId, cancellationToken), closure.ResourceId))
        {
            throw SchedulingAuthorization.Denied();
        }

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        closures.Remove(closure);
        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.ClosureDeleted,
            SchedulingAuditActions.Resources.Closure, closure.Id.Value, ["deleted"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ClosureDeletedResult();
    }
}
