using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Application;

public sealed record ListUomsQuery(Guid TenantId) : IQuery<UomsDto>;

/// <summary>Las unidades del tenant, las de sistema primero. El GET escribe sólo por el provisioner (P-8): es quien siembra las
/// seis del sistema en el primer acceso.</summary>
public sealed class ListUomsHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWmsProvisioner provisioner,
    IUnitOfMeasureRepository unitsOfMeasure)
    : IQueryHandler<ListUomsQuery, UomsDto>
{
    public async Task<UomsDto> HandleAsync(ListUomsQuery query, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(executionContext, query.TenantId, WmsPermissions.DocumentRead, ct: cancellationToken);
        await provisioner.EnsureAsync(query.TenantId, cancellationToken);

        var found = await unitsOfMeasure.ListAsync(query.TenantId, cancellationToken);

        return new UomsDto(found.Select(UomDtoMapper.ToDto).ToList());
    }
}

internal static class UomDtoMapper
{
    public static UomDto ToDto(UnitOfMeasure uom)
    {
        ArgumentNullException.ThrowIfNull(uom);

        return new UomDto(uom.Code.Value, uom.Name, uom.Precision, uom.IsSystem);
    }
}
