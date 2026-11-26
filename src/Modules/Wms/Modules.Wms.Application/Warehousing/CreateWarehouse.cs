using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>Aplanado (S25): el request no se anida en el comando, así la clave del mapa <c>errors</c> es <c>Code</c> y no
/// <c>Request.Code</c>, que es el campo que el formulario sabe marcar.</summary>
public sealed record CreateWarehouseCommand(Guid TenantId, string Code, string Name, Guid? CityId) : ICommand<WarehouseDto>
{
    public static CreateWarehouseCommand From(Guid tenantId, CreateWarehouseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Un campo ausente llega null aunque el record lo declare no nulo: se vuelve vacío para que lo atrape el validador.
        return new(tenantId, request.Code ?? string.Empty, request.Name ?? string.Empty, request.CityId);
    }
}

/// <summary>El dominio lanza <see cref="ArgumentException"/> con un nombre vacío o largo, y eso sería un 500: el validador cubre
/// esos casos para que sean 422 <c>validation.failed</c> con el campo. El formato del código (P-4) lo valida el dominio, que da
/// <c>wms.warehouse.code_invalid</c>.</summary>
public sealed class CreateWarehouseValidator : AbstractValidator<CreateWarehouseCommand>
{
    public CreateWarehouseValidator()
    {
        RuleFor(command => command.Code).MustBeAWarehouseCode();
        RuleFor(command => command.Name).MustBeAWarehouseName();
    }
}

internal static class WarehouseRules
{
    public static IRuleBuilderOptions<T, string> MustBeAWarehouseCode<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(code => !string.IsNullOrWhiteSpace(code) && code.Trim().Length <= Warehouse.CodeMaxLength)
            .WithMessage($"The code is required and has at most {Warehouse.CodeMaxLength} characters.");

    public static IRuleBuilderOptions<T, string> MustBeAWarehouseName<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(name => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= Warehouse.NameMaxLength)
            .WithMessage($"The name is required and has at most {Warehouse.NameMaxLength} characters.");

    /// <summary>Una ciudad que Geography no conoce es 422 y no una FK rota: Wms no tiene FK hacia otro esquema.</summary>
    public static async Task EnsureCityAsync(IWmsGeographyReader geography, Guid? cityId, CancellationToken cancellationToken)
    {
        if (cityId is not { } id)
        {
            return;
        }

        var cities = await geography.FindCitiesAsync([id], cancellationToken);
        if (!cities.ContainsKey(id))
        {
            throw new WmsDomainException(WmsErrorCodes.WarehouseCityUnknown, "The city does not exist.");
        }
    }
}

/// <summary>Crea la bodega con sus cuatro virtuales y la zona de recepción (RF-003), y la audita en la misma transacción.</summary>
public sealed class CreateWarehouseHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWarehouseRepository warehouses,
    IWmsGeographyReader geography,
    IWmsUnitOfWork unitOfWork,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IValidator<CreateWarehouseCommand> validator)
    : ICommandHandler<CreateWarehouseCommand, WarehouseDto>
{
    public async Task<WarehouseDto> HandleAsync(CreateWarehouseCommand command, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(executionContext, command.TenantId, WmsPermissions.WarehouseManage, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await WarehouseRules.EnsureCityAsync(geography, command.CityId, cancellationToken);

        var now = clock.UtcNow;
        var warehouse = Warehouse.Create(WarehouseId.New(), command.TenantId, command.Code, command.Name, command.CityId, now);
        warehouses.Add(warehouse);

        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            WarehouseCreated,
            Resources.Warehouse,
            warehouse.Id.Value.ToString(),
            new Dictionary<string, string?>
            {
                ["code"] = warehouse.Code,
                ["name"] = warehouse.Name,
                ["cityId"] = warehouse.CityId?.ToString(),
            },
            now);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await WarehouseDtoMapper.ToDtoAsync(warehouse, geography, cancellationToken);
    }
}
