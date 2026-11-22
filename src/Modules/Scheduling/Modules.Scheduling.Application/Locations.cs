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

/// <summary>La principal no se archiva: el dominio lo rechaza con <c>scheduling.location.main_cannot_be_archived</c> (422).</summary>
public sealed class ArchiveLocationHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ILocationRepository locations,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork)
    : ICommandHandler<ArchiveLocationCommand, LocationDto>
{
    public async Task<LocationDto> HandleAsync(ArchiveLocationCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.SettingsManage);

        var location = await locations.FindAsync(command.TenantId, new LocationId(command.LocationId), cancellationToken)
            ?? throw SchedulingNotFound.Location(command.LocationId);

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        location.Archive(now);
        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.LocationArchived,
            SchedulingAuditActions.Resources.Location, location.Id.Value, ["archived"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return SchedulingComposer.Location(location);
    }
}
