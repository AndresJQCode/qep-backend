using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

public sealed record ListBlocksQuery(Guid TenantId) : IQuery<IReadOnlyList<BlockDto>>;

public sealed record CreateBlockCommand(Guid TenantId, BlockRequest Request) : ICommand<BlockDto>;

/// <param name="IfMatch">La versión que la pantalla cargó; llega por <c>If-Match</c>, crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record UpdateBlockCommand(Guid TenantId, Guid BlockId, BlockRequest Request, string? IfMatch) : ICommand<BlockDto>;

public sealed record ArchiveBlockCommand(Guid TenantId, Guid BlockId) : ICommand<BlockDto>;

/// <summary>
/// Lo que repiten el alta y la edición de una franja. Sus ids de recursos y servicios no tienen llave foránea (plan, corrección 13): se
/// revisan acá, al guardar. El alcance va antes que la existencia, para que quien no ve todas las agendas reciba 403 por un recurso
/// ajeno y no un 422 que le diga si existe.
/// </summary>
internal static class BlockInput
{
    public static BlockDefinition ToDefinition(
        BlockRequest request, IReadOnlyList<ServiceId> serviceIds, IReadOnlyList<ResourceId> resourceIds)
    {
        Enum.TryParse(request.Kind, ignoreCase: false, out BlockKind kind);
        SessionMode? mode = SchedulingEnums.TryParse<SessionMode>(request.SessionMode, out var parsedMode) ? parsedMode : null;
        var rule = SchedulingEnums.TryParse<AssignmentRule>(request.AssignmentRule, out var parsedRule) ? parsedRule : AssignmentRule.FirstFree;

        return new BlockDefinition(
            request.Name!,
            kind,
            kind == BlockKind.Weekly
                ? [.. request.Weekly!.Select(day => new WeekdayHours(day.Day!.Value, Ranges(day.Ranges!)))]
                : [],
            kind == BlockKind.OneOff ? new DateHours(request.OneOff!.Date!.Value, Ranges(request.OneOff.Ranges!)) : null,
            request.ValidFrom,
            request.ValidTo,
            request.ClosesOnHolidays ?? false,
            request.CapacityPerResource ?? 1,
            mode,
            request.AllowClientChoice ?? false,
            rule,
            serviceIds,
            resourceIds);
    }

    // El validador ya comprobó que cada rango es válido: aquí sólo se convierte.
    private static IEnumerable<TimeRange> Ranges(IReadOnlyList<TimeRangeRequest> ranges) =>
        ranges.Select(range => new TimeRange(range.From!.Value, range.To!.Value));

    public static ResourceId[] ResourceIds(BlockRequest request) => [.. request.ResourceIds!.Select(id => new ResourceId(id))];

    public static ServiceId[] ServiceIds(BlockRequest request) => [.. request.ServiceIds!.Select(id => new ServiceId(id))];

    /// <summary>Quien no ve todas las agendas actúa sólo sobre franjas cuyos recursos son todos suyos.</summary>
    public static void EnsureInScope(ResourceScope scope, IEnumerable<ResourceId> resourceIds)
    {
        if (!resourceIds.All(id => scope.Includes(id.Value)))
        {
            throw SchedulingAuthorization.Denied();
        }
    }

    /// <summary>
    /// Que cada recurso y cada servicio existan en el tenant y no estén archivados, y que los recursos sean de una misma sede (la de la
    /// franja). Con <paramref name="current"/> sólo se revalidan los ids nuevos: editar una franja cuyo recurso luego se archivó no debe
    /// fallar por eso. Devuelve la sede de los recursos.
    /// </summary>
    public static async Task<LocationId> EnsureReferencesAsync(
        IResourceRepository resources,
        IServiceRepository services,
        Guid tenantId,
        IReadOnlyList<ResourceId> resourceIds,
        IReadOnlyList<ServiceId> serviceIds,
        AvailabilityBlock? current,
        CancellationToken cancellationToken)
    {
        var foundResources = await resources.FindManyAsync(tenantId, resourceIds, cancellationToken);
        var byId = foundResources.ToDictionary(resource => resource.Id);
        foreach (var id in resourceIds)
        {
            var kept = current?.ResourceIds.Contains(id) == true;
            if (!byId.TryGetValue(id, out var resource) || (resource.Archived && !kept))
            {
                SchedulingFieldErrors.Throw("resourceIds", "Algún recurso no existe en esta organización o está archivado.");
            }
        }

        var locations = foundResources.Select(resource => resource.LocationId).Distinct().ToArray();
        if (locations.Length != 1)
        {
            SchedulingFieldErrors.Throw("resourceIds", "Los recursos de una franja deben ser de la misma sede.");
        }

        if (current is not null && locations[0] != current.LocationId)
        {
            SchedulingFieldErrors.Throw("resourceIds", "La sede de una franja no cambia: los recursos deben ser de la misma sede.");
        }

        var foundServices = await services.FindManyAsync(tenantId, serviceIds, cancellationToken);
        var servicesById = foundServices.ToDictionary(service => service.Id);
        foreach (var id in serviceIds)
        {
            var kept = current?.ServiceIds.Contains(id) == true;
            if (!servicesById.TryGetValue(id, out var service) || (service.Archived && !kept))
            {
                SchedulingFieldErrors.Throw("serviceIds", "Algún servicio no existe en esta organización o está archivado.");
            }
        }

        return locations[0];
    }
}

/// <summary>
/// Archiving a resource or a service takes its id out of every block of the tenant, in the same transaction (the ids have no foreign
/// key, so nothing else would clean them). A block left without resources or services is archived. Every block touched leaves its own
/// audit entry; the caller saves.
/// </summary>
internal static class BlockDetachment
{
    public static Task DetachResourceAsync(
        IAvailabilityBlockRepository blocks,
        ISchedulingAuditPublisher auditPublisher,
        IExecutionContext executionContext,
        Guid tenantId,
        ResourceId resource,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        DetachAsync(blocks, auditPublisher, executionContext, tenantId, block => block.DetachResource(resource, now), "resourceIds", now, cancellationToken);

    public static Task DetachServiceAsync(
        IAvailabilityBlockRepository blocks,
        ISchedulingAuditPublisher auditPublisher,
        IExecutionContext executionContext,
        Guid tenantId,
        ServiceId service,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        DetachAsync(blocks, auditPublisher, executionContext, tenantId, block => block.DetachService(service, now), "serviceIds", now, cancellationToken);

    private static async Task DetachAsync(
        IAvailabilityBlockRepository blocks,
        ISchedulingAuditPublisher auditPublisher,
        IExecutionContext executionContext,
        Guid tenantId,
        Func<AvailabilityBlock, bool> detach,
        string field,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var block in await blocks.ListActiveForUpdateAsync(tenantId, cancellationToken))
        {
            if (!detach(block))
            {
                continue;
            }

            SchedulingAudit.Publish(
                auditPublisher, executionContext, tenantId, SchedulingAuditActions.BlockUpdated,
                SchedulingAuditActions.Resources.Block, block.Id.Value, [field], now);
            if (block.Archived)
            {
                SchedulingAudit.Publish(
                    auditPublisher, executionContext, tenantId, SchedulingAuditActions.BlockArchived,
                    SchedulingAuditActions.Resources.Block, block.Id.Value, ["archived"], now);
            }
        }
    }
}

public sealed class ListBlocksHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    SchedulingFirstAccess firstAccess,
    ResourceScopeResolver scopeResolver,
    IAvailabilityBlockRepository blocks)
    : IQueryHandler<ListBlocksQuery, IReadOnlyList<BlockDto>>
{
    public async Task<IReadOnlyList<BlockDto>> HandleAsync(ListBlocksQuery query, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AvailabilityManage);
        await firstAccess.EnsureAsync(query.TenantId, cancellationToken);

        // Quien no ve todas las agendas recibe las franjas que abren su agenda, aunque otro recurso las comparta; sólo edita las que son enteras suyas.
        var scope = await scopeResolver.ResolveAsync(query.TenantId, cancellationToken);
        return [.. (await blocks.ListAsync(query.TenantId, scope, cancellationToken)).Select(SchedulingComposer.Block)];
    }
}

public sealed class CreateBlockHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    SchedulingFirstAccess firstAccess,
    ResourceScopeResolver scopeResolver,
    IResourceRepository resources,
    IServiceRepository services,
    IAvailabilityBlockRepository blocks,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork,
    IValidator<BlockRequest> validator)
    : ICommandHandler<CreateBlockCommand, BlockDto>
{
    public async Task<BlockDto> HandleAsync(CreateBlockCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AvailabilityManage);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);
        await firstAccess.EnsureAsync(command.TenantId, cancellationToken);

        var resourceIds = BlockInput.ResourceIds(command.Request);
        var serviceIds = BlockInput.ServiceIds(command.Request);
        BlockInput.EnsureInScope(await scopeResolver.ResolveAsync(command.TenantId, cancellationToken), resourceIds);
        var locationId = await BlockInput.EnsureReferencesAsync(
            resources, services, command.TenantId, resourceIds, serviceIds, null, cancellationToken);

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var block = AvailabilityBlock.Create(
            AvailabilityBlockId.New(), command.TenantId, locationId, BlockInput.ToDefinition(command.Request, serviceIds, resourceIds), now);
        blocks.Add(block);
        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.BlockCreated,
            SchedulingAuditActions.Resources.Block, block.Id.Value, ["name", "kind", "schedule", "capacityPerResource", "resourceIds", "serviceIds"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return SchedulingComposer.Block(block);
    }
}

public sealed class UpdateBlockHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ResourceScopeResolver scopeResolver,
    IResourceRepository resources,
    IServiceRepository services,
    IAvailabilityBlockRepository blocks,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork,
    IValidator<BlockRequest> validator)
    : ICommandHandler<UpdateBlockCommand, BlockDto>
{
    public async Task<BlockDto> HandleAsync(UpdateBlockCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AvailabilityManage);
        var expectedVersion = SchedulingIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var block = await blocks.FindAsync(command.TenantId, new AvailabilityBlockId(command.BlockId), cancellationToken)
            ?? throw SchedulingNotFound.Block(command.BlockId);

        // Tanto la franja guardada como la pedida deben ser enteras del alcance: no se le quita ni se le agrega un recurso ajeno a nadie.
        var scope = await scopeResolver.ResolveAsync(command.TenantId, cancellationToken);
        var resourceIds = BlockInput.ResourceIds(command.Request);
        var serviceIds = BlockInput.ServiceIds(command.Request);
        BlockInput.EnsureInScope(scope, block.ResourceIds);
        BlockInput.EnsureInScope(scope, resourceIds);

        SchedulingIfMatch.EnsureVersion(block.Version, expectedVersion);
        await BlockInput.EnsureReferencesAsync(resources, services, command.TenantId, resourceIds, serviceIds, block, cancellationToken);

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var definition = BlockInput.ToDefinition(command.Request, serviceIds, resourceIds);
        var changedFields = BlockChanges.Between(block, definition);
        block.Update(definition, now);
        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.BlockUpdated,
            SchedulingAuditActions.Resources.Block, block.Id.Value, changedFields, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return SchedulingComposer.Block(block);
    }
}

/// <summary>Archivar una franja deja de ofrecer nuevos espacios; las citas ya tomadas siguen (viven en su sesión).</summary>
public sealed class ArchiveBlockHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ResourceScopeResolver scopeResolver,
    IAvailabilityBlockRepository blocks,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork)
    : ICommandHandler<ArchiveBlockCommand, BlockDto>
{
    public async Task<BlockDto> HandleAsync(ArchiveBlockCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AvailabilityManage);

        var block = await blocks.FindAsync(command.TenantId, new AvailabilityBlockId(command.BlockId), cancellationToken)
            ?? throw SchedulingNotFound.Block(command.BlockId);
        BlockInput.EnsureInScope(await scopeResolver.ResolveAsync(command.TenantId, cancellationToken), block.ResourceIds);

        // Archiving twice answers the same block and changes (and audits) nothing.
        if (block.Archived)
        {
            return SchedulingComposer.Block(block);
        }

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        block.Archive(now);
        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.BlockArchived,
            SchedulingAuditActions.Resources.Block, block.Id.Value, ["archived"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return SchedulingComposer.Block(block);
    }
}
