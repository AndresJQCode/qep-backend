using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

public sealed record ListResourcesQuery(Guid TenantId) : IQuery<IReadOnlyList<ResourceDto>>;

public sealed record CreateResourceCommand(Guid TenantId, ResourceRequest Request) : ICommand<ResourceDto>;

/// <param name="IfMatch">La versión que la pantalla cargó; llega por <c>If-Match</c>, crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record UpdateResourceCommand(Guid TenantId, Guid ResourceId, ResourceRequest Request, string? IfMatch) : ICommand<ResourceDto>;

public sealed record ArchiveResourceCommand(Guid TenantId, Guid ResourceId) : ICommand<ResourceDto>;

/// <summary>Lo que repiten el alta y la edición: el vínculo a un miembro se valida contra el tenant y contra la sede.</summary>
internal static class ResourceMemberLink
{
    public static async Task EnsureAsync(
        ISchedulingMemberDirectory members,
        IResourceRepository resources,
        Guid tenantId,
        LocationId locationId,
        Guid? memberId,
        Guid? currentMemberId,
        ResourceId? resourceId,
        CancellationToken cancellationToken)
    {
        // Sólo un vínculo nuevo se revalida: editar un recurso cuyo miembro luego se desactivó no debe fallar por eso.
        if (memberId is not { } id || id == currentMemberId)
        {
            return;
        }

        // Una membresía de otro tenant no aparece: el servidor no dice si existe en otro lado.
        var found = await members.FindAsync(tenantId, [id], cancellationToken);
        var member = found.Count == 0 ? null : found[0];
        if (member is null)
        {
            SchedulingFieldErrors.Throw("memberId", "El miembro no existe en esta organización.");
        }

        if (!member.IsActive)
        {
            SchedulingFieldErrors.Throw("memberId", "El miembro no está activo.");
        }

        if (await resources.MemberIsLinkedAsync(tenantId, locationId, id, resourceId, cancellationToken))
        {
            SchedulingFieldErrors.Throw("memberId", "Ese miembro ya atiende otro recurso de esta sede.");
        }
    }
}

public sealed class ListResourcesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    SchedulingFirstAccess firstAccess,
    ResourceScopeResolver scopeResolver,
    IResourceRepository resources,
    SchedulingComposer composer)
    : IQueryHandler<ListResourcesQuery, IReadOnlyList<ResourceDto>>
{
    public async Task<IReadOnlyList<ResourceDto>> HandleAsync(ListResourcesQuery query, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, SchedulingPermissions.AppointmentRead);
        await firstAccess.EnsureAsync(query.TenantId, cancellationToken);

        // El alcance lo aplica la consulta: quien no ve todas las agendas recibe sólo sus recursos, no la lista completa filtrada en pantalla.
        var scope = await scopeResolver.ResolveAsync(query.TenantId, cancellationToken);
        var list = await resources.ListAsync(query.TenantId, scope, cancellationToken);
        return await composer.ResourcesAsync(query.TenantId, list, cancellationToken);
    }
}

public sealed class CreateResourceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    SchedulingFirstAccess firstAccess,
    ILocationRepository locations,
    IResourceRepository resources,
    ISchedulingMemberDirectory members,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork,
    SchedulingComposer composer,
    IValidator<ResourceRequest> validator)
    : ICommandHandler<CreateResourceCommand, ResourceDto>
{
    public async Task<ResourceDto> HandleAsync(CreateResourceCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.SettingsManage);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);
        await firstAccess.EnsureAsync(command.TenantId, cancellationToken);

        var request = command.Request;
        // Sin sede, la principal: el caso de «un consultorio, una sede» no obliga a la pantalla a pedir un id que ya conoce el servidor.
        var location = request.LocationId is { } locationId
            ? await locations.FindAsync(command.TenantId, new LocationId(locationId), cancellationToken)
            : await locations.FindMainAsync(command.TenantId, cancellationToken);
        if (location is null || location.Archived)
        {
            SchedulingFieldErrors.Throw("locationId", "La sede no existe o está archivada.");
        }

        await ResourceMemberLink.EnsureAsync(
            members, resources, command.TenantId, location.Id, request.MemberId, null, null, cancellationToken);

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var resource = Resource.Create(ResourceId.New(), command.TenantId, location.Id, request.DisplayName!, request.MemberId, now);
        resources.Add(resource);
        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.ResourceCreated,
            SchedulingAuditActions.Resources.Resource, resource.Id.Value, ["displayName", "memberId"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ResourceAsync(command.TenantId, resource, cancellationToken);
    }
}

/// <summary>
/// El nombre y el vínculo de un recurso los edita quien administra la estructura (<c>settings.manage</c>) o el dueño de <b>ese</b> recurso
/// (<c>availability.manage</c> dentro de su alcance). Un recurso ajeno responde 403, nunca 404.
/// </summary>
public sealed class UpdateResourceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ResourceScopeResolver scopeResolver,
    IResourceRepository resources,
    ISchedulingMemberDirectory members,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork,
    SchedulingComposer composer,
    IValidator<ResourceRequest> validator)
    : ICommandHandler<UpdateResourceCommand, ResourceDto>
{
    public async Task<ResourceDto> HandleAsync(UpdateResourceCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, SchedulingPermissions.AppointmentRead);
        var structural = executionContext.HasPermission(SchedulingPermissions.SettingsManage);
        if (!structural && !executionContext.HasPermission(SchedulingPermissions.AvailabilityManage))
        {
            throw SchedulingAuthorization.Denied();
        }

        var expectedVersion = SchedulingIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var resource = await resources.FindAsync(command.TenantId, new ResourceId(command.ResourceId), cancellationToken)
            ?? throw SchedulingNotFound.Resource(command.ResourceId);
        if (!structural
            && !(await scopeResolver.ResolveAsync(command.TenantId, cancellationToken)).Includes(command.ResourceId))
        {
            throw SchedulingAuthorization.Denied();
        }

        SchedulingIfMatch.EnsureVersion(resource.Version, expectedVersion);
        var request = command.Request;
        if (request.LocationId is { } locationId && locationId != resource.LocationId.Value)
        {
            SchedulingFieldErrors.Throw("locationId", "La sede de un recurso no cambia.");
        }

        await ResourceMemberLink.EnsureAsync(
            members, resources, command.TenantId, resource.LocationId, request.MemberId, resource.MemberId, resource.Id, cancellationToken);

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var changed = new List<string>();
        if (resource.DisplayName != request.DisplayName!.Trim())
        {
            changed.Add("displayName");
        }

        if (resource.MemberId != request.MemberId)
        {
            changed.Add("memberId");
        }

        resource.Update(request.DisplayName!, request.MemberId, now);
        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.ResourceUpdated,
            SchedulingAuditActions.Resources.Resource, resource.Id.Value, changed, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ResourceAsync(command.TenantId, resource, cancellationToken);
    }
}

/// <summary>
/// Archivar no borra su historial; se rechaza mientras tenga citas futuras vigentes (<c>Pending</c> o <c>Confirmed</c>). Al archivar, el
/// recurso sale de todas las franjas del tenant y la que se quede sin recursos se archiva.
/// </summary>
public sealed class ArchiveResourceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IResourceRepository resources,
    IAvailabilityBlockRepository blocks,
    IAppointmentRepository appointments,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork,
    SchedulingComposer composer)
    : ICommandHandler<ArchiveResourceCommand, ResourceDto>
{
    public async Task<ResourceDto> HandleAsync(ArchiveResourceCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.SettingsManage);

        var resource = await resources.FindAsync(command.TenantId, new ResourceId(command.ResourceId), cancellationToken)
            ?? throw SchedulingNotFound.Resource(command.ResourceId);

        // Archiving twice answers the same resource and changes (and audits) nothing.
        if (resource.Archived)
        {
            return await composer.ResourceAsync(command.TenantId, resource, cancellationToken);
        }

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;

        // The resource's row lock is the one bookings take: a booking already inside commits before the check below, and one that arrives
        // later re-reads the resource as archived once it holds the lock.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await appointments.LockResourcesAsync(command.TenantId, [resource.Id], cancellationToken);
        if (await resources.HasFutureAppointmentsAsync(command.TenantId, resource.Id, now, cancellationToken))
        {
            throw new SchedulingDomainException(
                SchedulingErrorCodes.ResourceHasFutureAppointments,
                "Este recurso tiene citas futuras. Cancélalas o reprográmalas antes de archivarlo.");
        }

        resource.Archive(now);
        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.ResourceArchived,
            SchedulingAuditActions.Resources.Resource, resource.Id.Value, ["archived"], now);
        await BlockDetachment.DetachResourceAsync(
            blocks, auditPublisher, executionContext, command.TenantId, resource.Id, now, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await composer.ResourceAsync(command.TenantId, resource, cancellationToken);
    }
}
