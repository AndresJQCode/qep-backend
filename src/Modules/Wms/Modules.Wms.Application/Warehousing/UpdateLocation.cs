using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>Aplanado como <see cref="CreateLocationCommand"/> (S25).</summary>
public sealed record UpdateLocationCommand(
    Guid TenantId,
    Guid WarehouseId,
    Guid LocationId,
    Guid? ParentId,
    string Code,
    string Kind,
    string Role,
    bool AllowsStorage,
    bool AllowsPicking,
    decimal? MaxWeightKg,
    decimal? MaxVolumeM3,
    int? MaxUnits,
    int PutawayPriority,
    IReadOnlyList<string>? StorageClasses,
    string Status,
    long Version)
    : ICommand<LocationDto>
{
    public static UpdateLocationCommand From(Guid tenantId, Guid warehouseId, Guid locationId, UpdateLocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new(
            tenantId,
            warehouseId,
            locationId,
            request.ParentId,
            request.Code ?? string.Empty,
            request.Kind ?? string.Empty,
            request.Role ?? string.Empty,
            request.AllowsStorage,
            request.AllowsPicking,
            request.MaxWeightKg,
            request.MaxVolumeM3,
            request.MaxUnits,
            request.PutawayPriority,
            request.StorageClasses,
            request.Status ?? string.Empty,
            request.Version);
    }
}

public sealed class UpdateLocationValidator : AbstractValidator<UpdateLocationCommand>
{
    public UpdateLocationValidator()
    {
        RuleFor(command => command.Code).MustBeALocationCode();
        RuleFor(command => command.Kind).MustBeAnEnumName<UpdateLocationCommand, LocationKind>();
        RuleFor(command => command.Role).MustBeAnEnumName<UpdateLocationCommand, LocationRole>();
        RuleFor(command => command.Status).MustBeAnEnumName<UpdateLocationCommand, LocationStatus>();
        RuleFor(command => command.PutawayPriority).MustBeAPutawayPriority();
        RuleFor(command => command.MaxWeightKg).MustFitTheWeightColumn();
        RuleFor(command => command.MaxVolumeM3).MustFitTheVolumeColumn();
        RuleForEach(command => command.StorageClasses).MustBeAStorageClass();
    }
}

public sealed class UpdateLocationHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWarehouseRepository warehouses,
    IWmsUnitOfWork unitOfWork,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IValidator<UpdateLocationCommand> validator)
    : ICommandHandler<UpdateLocationCommand, LocationDto>
{
    public async Task<LocationDto> HandleAsync(UpdateLocationCommand command, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.WarehouseManage, warehouseId: command.WarehouseId, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var warehouse = await warehouses.GetAsync(command.TenantId, new WarehouseId(command.WarehouseId), cancellationToken)
            ?? throw WmsNotFound.Warehouse(command.WarehouseId);
        // El dominio lanza InvalidOperationException (500) si el id no es de la bodega: se responde 404 antes de llamarlo.
        var location = warehouse.FindLocation(new LocationId(command.LocationId))
            ?? throw WmsNotFound.Location(command.LocationId);

        // S22: una virtual es de solo lectura por PUT sin mirar el cuerpo, ni siquiera la versión; si no, una versión vieja daría
        // un 412 que invita a reintentar algo que nunca va a funcionar.
        if (location.IsVirtual)
        {
            throw new WmsDomainException(WmsErrorCodes.LocationVirtualImmutable, "A virtual location cannot be edited.");
        }

        WmsVersions.Ensure(location.Version, command.Version);

        var before = LocationRules.Snapshot(location);
        var now = clock.UtcNow;
        warehouse.UpdateLocation(
            location.Id,
            command.ParentId is { } parentId ? new LocationId(parentId) : null,
            command.Code,
            LocationRules.Parse<LocationKind>(command.Kind),
            LocationRules.Parse<LocationRole>(command.Role),
            command.AllowsStorage,
            command.AllowsPicking,
            LocationRules.Capacity(command.MaxWeightKg, command.MaxVolumeM3, command.MaxUnits),
            command.PutawayPriority,
            LocationRules.Classes(command.StorageClasses),
            LocationRules.Parse<LocationStatus>(command.Status),
            now);

        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            LocationUpdated,
            Resources.Location,
            location.Id.Value.ToString(),
            LocationRules.Diff(before, LocationRules.Snapshot(location)),
            now);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return LocationDtoMapper.ToDto(location);
    }
}
