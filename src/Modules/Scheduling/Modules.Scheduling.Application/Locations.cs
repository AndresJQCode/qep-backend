using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

public sealed record ListLocationsQuery(Guid TenantId) : IQuery<IReadOnlyList<LocationDto>>;

public sealed record CreateLocationCommand(Guid TenantId, LocationRequest Request) : ICommand<LocationDto>;

/// <param name="IfMatch">La versión que la pantalla cargó; llega por <c>If-Match</c>, crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record UpdateLocationCommand(Guid TenantId, Guid LocationId, LocationRequest Request, string? IfMatch) : ICommand<LocationDto>;

public sealed record ArchiveLocationCommand(Guid TenantId, Guid LocationId) : ICommand<LocationDto>;

public sealed class ListLocationsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    SchedulingFirstAccess firstAccess,
    ILocationRepository locations)
    : IQueryHandler<ListLocationsQuery, IReadOnlyList<LocationDto>>
{
    public async Task<IReadOnlyList<LocationDto>> HandleAsync(ListLocationsQuery query, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, SchedulingPermissions.AppointmentRead);
        await firstAccess.EnsureAsync(query.TenantId, cancellationToken);

        return [.. (await locations.ListAsync(query.TenantId, cancellationToken)).Select(SchedulingComposer.Location)];
    }
}

/// <summary>Una sede nueva nunca es la principal: la única la aprovisionó el primer acceso, y el índice único parcial lo respalda.</summary>
public sealed class CreateLocationHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    SchedulingFirstAccess firstAccess,
    ILocationRepository locations,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork,
    IValidator<LocationRequest> validator)
    : ICommandHandler<CreateLocationCommand, LocationDto>
{
    public async Task<LocationDto> HandleAsync(CreateLocationCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.SettingsManage);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);
        await firstAccess.EnsureAsync(command.TenantId, cancellationToken);

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var location = Location.Create(LocationId.New(), command.TenantId, command.Request.Name!, isMain: false, now);
        locations.Add(location);
        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.LocationCreated,
            SchedulingAuditActions.Resources.Location, location.Id.Value, ["name"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return SchedulingComposer.Location(location);
    }
}

public sealed class UpdateLocationHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ILocationRepository locations,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork,
    IValidator<LocationRequest> validator)
    : ICommandHandler<UpdateLocationCommand, LocationDto>
{
    public async Task<LocationDto> HandleAsync(UpdateLocationCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.SettingsManage);
        var expectedVersion = SchedulingIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var location = await locations.FindAsync(command.TenantId, new LocationId(command.LocationId), cancellationToken)
            ?? throw SchedulingNotFound.Location(command.LocationId);
        SchedulingIfMatch.EnsureVersion(location.Version, expectedVersion);

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        location.Rename(command.Request.Name!, now);
        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.LocationUpdated,
            SchedulingAuditActions.Resources.Location, location.Id.Value, ["name"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return SchedulingComposer.Location(location);
    }
}

/// <summary>
/// Archiva la sede <b>en cascada y sin vuelta atrás</b> (plan, corrección 50; decisión del owner). En una sola transacción, con el candado de
/// reserva de todos los recursos de la sede (el mismo que toman reservar, cancelar y archivar un recurso): cancela las citas futuras vigentes
/// (<c>Pending</c> o <c>Confirmed</c>, las que empiezan después de ahora; las pasadas y las ya cerradas no se tocan) liberando su cupo, archiva
/// los recursos no archivados de la sede (sacándolos de las franjas, como <see cref="ArchiveResourceHandler"/>), archiva las franjas que
/// siguen vigentes en la sede y, por último, la sede. <b>No hay desarchivado de la cascada:</b> ni la API ni el dominio exponen cómo traer
/// de vuelta la sede, los recursos, las franjas ni las citas canceladas; reabrir una sede es crear una nueva. La pantalla debe avisarlo antes.
/// Auditoría atómica: una entrada por entidad que cambió de verdad (sede, recursos, franjas y cada cita cancelada); las citas dejan además su
/// línea <c>Cancelled</c> en su historial. Archivarla de nuevo no hace ni audita nada. La principal no se archiva: el dominio lo rechaza con
/// <c>scheduling.location.main_cannot_be_archived</c> (422) y nada llega a guardarse.
/// </summary>
public sealed class ArchiveLocationHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ILocationRepository locations,
    IResourceRepository resources,
    IAvailabilityBlockRepository blocks,
    IAppointmentRepository appointments,
    ISessionRepository sessions,
    ISchedulingMemberDirectory members,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork)
    : ICommandHandler<ArchiveLocationCommand, LocationDto>
{
    /// <summary>The reason the cancelled appointments carry in their history (shown to staff, so it is product text).</summary>
    public const string CancelReason = "La sede fue archivada.";

    public async Task<LocationDto> HandleAsync(ArchiveLocationCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.SettingsManage);

        var location = await locations.FindAsync(command.TenantId, new LocationId(command.LocationId), cancellationToken)
            ?? throw SchedulingNotFound.Location(command.LocationId);

        // Archiving twice answers the same location and changes (and audits) nothing.
        if (location.Archived)
        {
            return SchedulingComposer.Location(location);
        }

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var actor = (await members.FindBySubjectAsync(command.TenantId, executionContext.SubjectId, cancellationToken))?.MemberId;

        // The domain rejects the main location here, before anything else changes (the audit entries are published only after).
        location.Archive(now);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        // Con seguimiento: los recursos se archivan abajo y ese cambio se guarda con el resto (ListAsync lee sin seguimiento).
        var locationResources = await resources.ListByLocationForUpdateAsync(command.TenantId, location.Id, cancellationToken);
        // Archived resources are locked too: nothing new can book on them, but a cancel or a reschedule of an old appointment may be running.
        await appointments.LockResourcesAsync(command.TenantId, [.. locationResources.Select(resource => resource.Id)], cancellationToken);

        var live = locationResources.Where(resource => !resource.Archived).ToArray();
        foreach (var appointment in await appointments.ListFutureLiveAsync(
            command.TenantId, [.. live.Select(resource => resource.Id)], now, cancellationToken))
        {
            var session = await sessions.FindAsync(command.TenantId, appointment.SessionId, cancellationToken)
                ?? throw new InvalidOperationException($"The session of appointment '{appointment.Id.Value}' does not exist.");
            appointment.Cancel(CancelReason, actor, now);
            session.Release(now);
            SchedulingAudit.Publish(
                auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.AppointmentCancelled,
                SchedulingAuditActions.Resources.Appointment, appointment.Id.Value, ["status", "cancelReason"], now);
        }

        foreach (var resource in live)
        {
            resource.Archive(now);
            SchedulingAudit.Publish(
                auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.ResourceArchived,
                SchedulingAuditActions.Resources.Resource, resource.Id.Value, ["archived"], now);
            await BlockDetachment.DetachResourceAsync(
                blocks, auditPublisher, executionContext, command.TenantId, resource.Id, now, cancellationToken);
        }

        // Blocks left without resources were archived by the detachment above (and audited there); this closes the rest of the location's.
        foreach (var block in (await blocks.ListActiveForUpdateAsync(command.TenantId, cancellationToken))
            .Where(block => block.LocationId == location.Id && !block.Archived))
        {
            block.Archive(now);
            SchedulingAudit.Publish(
                auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.BlockArchived,
                SchedulingAuditActions.Resources.Block, block.Id.Value, ["archived"], now);
        }

        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.LocationArchived,
            SchedulingAuditActions.Resources.Location, location.Id.Value, ["archived"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return SchedulingComposer.Location(location);
    }
}
