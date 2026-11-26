using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>Aplanado (S25): las claves del mapa <c>errors</c> son <c>Code</c>, <c>Kind</c>, <c>PutawayPriority</c>, no
/// <c>Request.Code</c>.</summary>
public sealed record CreateLocationCommand(
    Guid TenantId,
    Guid WarehouseId,
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
    IReadOnlyList<string>? StorageClasses)
    : ICommand<LocationDto>
{
    public static CreateLocationCommand From(Guid tenantId, Guid warehouseId, LocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Un texto ausente llega null aunque el record lo declare no nulo: se vuelve vacío para que lo atrape el validador.
        return new(
            tenantId,
            warehouseId,
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
            request.StorageClasses);
    }
}

/// <summary>Cubre lo que el dominio lanzaría como <see cref="ArgumentException"/> o no podría parsear (500); las reglas de negocio
/// (formato del código, jerarquía, capacidad negativa) las da el dominio con su código <c>wms.location.*</c>.</summary>
public sealed class CreateLocationValidator : AbstractValidator<CreateLocationCommand>
{
    public CreateLocationValidator()
    {
        RuleFor(command => command.Code).MustBeALocationCode();
        RuleFor(command => command.Kind).MustBeAnEnumName<CreateLocationCommand, LocationKind>();
        RuleFor(command => command.Role).MustBeAnEnumName<CreateLocationCommand, LocationRole>();
        RuleFor(command => command.PutawayPriority).MustBeAPutawayPriority();
        RuleFor(command => command.MaxWeightKg).MustFitTheWeightColumn();
        RuleFor(command => command.MaxVolumeM3).MustFitTheVolumeColumn();
        RuleForEach(command => command.StorageClasses).MustBeAStorageClass();
    }
}

public sealed class CreateLocationHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWarehouseRepository warehouses,
    IWmsUnitOfWork unitOfWork,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IValidator<CreateLocationCommand> validator)
    : ICommandHandler<CreateLocationCommand, LocationDto>
{
    public async Task<LocationDto> HandleAsync(CreateLocationCommand command, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.WarehouseManage, warehouseId: command.WarehouseId, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var warehouse = await warehouses.GetAsync(command.TenantId, new WarehouseId(command.WarehouseId), cancellationToken)
            ?? throw WmsNotFound.Warehouse(command.WarehouseId);

        var now = clock.UtcNow;
        var location = warehouse.AddLocation(
            LocationId.New(),
            command.ParentId is { } parentId ? new LocationId(parentId) : null,
            command.Code,
            LocationRules.Parse<LocationKind>(command.Kind),
            LocationRules.Parse<LocationRole>(command.Role),
            command.AllowsStorage,
            command.AllowsPicking,
            LocationRules.Capacity(command.MaxWeightKg, command.MaxVolumeM3, command.MaxUnits),
            command.PutawayPriority,
            LocationRules.Classes(command.StorageClasses),
            now);

        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            LocationCreated,
            Resources.Location,
            location.Id.Value.ToString(),
            new Dictionary<string, string?>
            {
                ["warehouseId"] = warehouse.Id.Value.ToString(),
                ["code"] = location.Code,
                ["path"] = location.Path,
                ["kind"] = location.Kind.ToString(),
                ["role"] = location.Role.ToString(),
            },
            now);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return LocationDtoMapper.ToDto(location);
    }
}
