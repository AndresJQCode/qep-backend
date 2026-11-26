using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

public sealed record ListServicesQuery(Guid TenantId) : IQuery<IReadOnlyList<ServiceDto>>;

public sealed record CreateServiceCommand(Guid TenantId, ServiceRequest Request) : ICommand<ServiceDto>;

/// <param name="IfMatch">La versión que la pantalla cargó; llega por <c>If-Match</c>, crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record UpdateServiceCommand(Guid TenantId, Guid ServiceId, ServiceRequest Request, string? IfMatch) : ICommand<ServiceDto>;

public sealed record ArchiveServiceCommand(Guid TenantId, Guid ServiceId) : ICommand<ServiceDto>;

public sealed class ListServicesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    SchedulingFirstAccess firstAccess,
    IServiceRepository services)
    : IQueryHandler<ListServicesQuery, IReadOnlyList<ServiceDto>>
{
    public async Task<IReadOnlyList<ServiceDto>> HandleAsync(ListServicesQuery query, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, SchedulingPermissions.AppointmentRead);
        await firstAccess.EnsureAsync(query.TenantId, cancellationToken);

        return [.. (await services.ListAsync(query.TenantId, cancellationToken)).Select(SchedulingComposer.Service)];
    }
}

public sealed class CreateServiceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    SchedulingFirstAccess firstAccess,
    IServiceRepository services,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork,
    IValidator<ServiceRequest> validator)
    : ICommandHandler<CreateServiceCommand, ServiceDto>
{
    public async Task<ServiceDto> HandleAsync(CreateServiceCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.SettingsManage);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);
        await firstAccess.EnsureAsync(command.TenantId, cancellationToken);

        var request = command.Request;
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var service = Service.Create(
            ServiceId.New(), command.TenantId, request.Name!, request.DurationMinutes!.Value, request.BufferMinutes ?? 0,
            request.BookableByChannel ?? false, now);
        services.Add(service);
        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.ServiceCreated,
            SchedulingAuditActions.Resources.Service, service.Id.Value, ["name", "durationMinutes", "bufferMinutes", "bookableByChannel"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return SchedulingComposer.Service(service);
    }
}

public sealed class UpdateServiceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IServiceRepository services,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork,
    IValidator<ServiceRequest> validator)
    : ICommandHandler<UpdateServiceCommand, ServiceDto>
{
    public async Task<ServiceDto> HandleAsync(UpdateServiceCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.SettingsManage);
        var expectedVersion = SchedulingIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var service = await services.FindAsync(command.TenantId, new ServiceId(command.ServiceId), cancellationToken)
            ?? throw SchedulingNotFound.Service(command.ServiceId);
        SchedulingIfMatch.EnsureVersion(service.Version, expectedVersion);

        var request = command.Request;
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var changed = new List<string>();
        if (service.Name != request.Name!.Trim())
        {
            changed.Add("name");
        }

        if (service.DurationMinutes != request.DurationMinutes!.Value)
        {
            changed.Add("durationMinutes");
        }

        if (service.BufferMinutes != (request.BufferMinutes ?? 0))
        {
            changed.Add("bufferMinutes");
        }

        if (service.BookableByChannel != (request.BookableByChannel ?? false))
        {
            changed.Add("bookableByChannel");
        }

        service.Update(
            request.Name!, request.DurationMinutes!.Value, request.BufferMinutes ?? 0, request.BookableByChannel ?? false, now);
        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.ServiceUpdated,
            SchedulingAuditActions.Resources.Service, service.Id.Value, changed, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return SchedulingComposer.Service(service);
    }
}

/// <summary>
/// Se rechaza mientras tenga citas futuras vigentes (<c>scheduling.service.in_use</c>, 422): su historial sigue apuntándole. Al archivar,
/// el servicio sale de todas las franjas del tenant y la que se quede sin servicios se archiva.
/// </summary>
public sealed class ArchiveServiceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IServiceRepository services,
    IResourceRepository resources,
    IAppointmentRepository appointments,
    IAvailabilityBlockRepository blocks,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork)
    : ICommandHandler<ArchiveServiceCommand, ServiceDto>
{
    public async Task<ServiceDto> HandleAsync(ArchiveServiceCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.SettingsManage);

        var service = await services.FindAsync(command.TenantId, new ServiceId(command.ServiceId), cancellationToken)
            ?? throw SchedulingNotFound.Service(command.ServiceId);

        // Archiving twice answers the same service and changes (and audits) nothing.
        if (service.Archived)
        {
            return SchedulingComposer.Service(service);
        }

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;

        // A service is offered through blocks, which name resources: the minimum that serializes with every booking of this service is the
        // lock of the resources that offer it, and a block can be edited to offer it elsewhere while this runs. So it takes the lock of every
        // resource of the tenant (archived ones included, their appointments still count), in id order like bookings do. Archiving a service
        // is rare and a tenant has few resources.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var resourceIds = (await resources.ListAsync(command.TenantId, ResourceScope.All, cancellationToken))
            .Select(resource => resource.Id)
            .ToArray();
        await appointments.LockResourcesAsync(command.TenantId, resourceIds, cancellationToken);
        if (await services.HasFutureAppointmentsAsync(command.TenantId, service.Id, now, cancellationToken))
        {
            throw new SchedulingDomainException(
                SchedulingErrorCodes.ServiceInUse,
                "Este servicio tiene citas futuras. Cancélalas o reprográmalas antes de archivarlo.");
        }

        service.Archive(now);
        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.ServiceArchived,
            SchedulingAuditActions.Resources.Service, service.Id.Value, ["archived"], now);
        await BlockDetachment.DetachServiceAsync(
            blocks, auditPublisher, executionContext, command.TenantId, service.Id, now, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return SchedulingComposer.Service(service);
    }
}
