using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

public sealed record LocationLabelsPdf(byte[] Content, string FileName);

public sealed record RenderLocationLabelsCommand(Guid TenantId, Guid WarehouseId, IReadOnlyList<Guid> LocationIds)
    : ICommand<LocationLabelsPdf>
{
    public const int MaxLabels = 500;

    public static RenderLocationLabelsCommand From(Guid tenantId, Guid warehouseId, LocationLabelsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Una lista ausente llega null aunque el record la declare no nula: se vuelve vacía para que lo atrape el validador.
        return new(tenantId, warehouseId, request.LocationIds ?? []);
    }
}

public sealed class RenderLocationLabelsValidator : AbstractValidator<RenderLocationLabelsCommand>
{
    public RenderLocationLabelsValidator() =>
        RuleFor(command => command.LocationIds)
            .Must(ids => ids.Count is >= 1 and <= RenderLocationLabelsCommand.MaxLabels)
            .WithMessage($"Between 1 and {RenderLocationLabelsCommand.MaxLabels} locations are required.");
}

/// <summary>
/// RF-004. Un id que no es de la bodega es 422 <c>wms.location.not_found</c> y no 404: la ruta existe, el que está mal es el cuerpo.
/// Las etiquetas salen en el orden en que llegaron los ids, para que quien imprime controle el orden del rollo.
/// </summary>
public sealed class RenderLocationLabelsHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWarehouseRepository warehouses,
    IWmsLabelRenderer renderer,
    IValidator<RenderLocationLabelsCommand> validator)
    : ICommandHandler<RenderLocationLabelsCommand, LocationLabelsPdf>
{
    public async Task<LocationLabelsPdf> HandleAsync(RenderLocationLabelsCommand command, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.WarehouseManage, warehouseId: command.WarehouseId, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var warehouse = await warehouses.GetAsync(command.TenantId, new WarehouseId(command.WarehouseId), cancellationToken)
            ?? throw WmsNotFound.Warehouse(command.WarehouseId);

        var labels = new List<LocationLabel>();
        foreach (var id in command.LocationIds.Distinct())
        {
            var location = warehouse.FindLocation(new LocationId(id))
                ?? throw new WmsDomainException(WmsErrorCodes.LocationNotFound, $"Location '{id}' does not belong to this warehouse.");
            // Una virtual no se escanea (ToScannable la excluye): su etiqueta impresa nunca resolvería en la app.
            if (location.IsVirtual)
            {
                throw new WmsDomainException(WmsErrorCodes.LocationVirtualImmutable, $"Location '{id}' is virtual and has no label.");
            }

            labels.Add(new LocationLabel(location.Code, location.Path, warehouse.Code));
        }

        var content = await renderer.RenderLocationLabelsAsync(labels, cancellationToken);

        return new LocationLabelsPdf(content, $"etiquetas-{warehouse.Code}.pdf");
    }
}
