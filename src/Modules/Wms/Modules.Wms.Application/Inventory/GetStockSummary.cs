using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Wms.Application;

public sealed record GetStockSummaryQuery(Guid TenantId, Guid? WarehouseId, Guid? OwnerId, Guid? VariantId) : IQuery<StockSummaryDto>;

/// <summary>El resumen de existencias (RF-021). Todo sale de <c>wms.v_stock_summary</c>; <c>Expected</c> se compone acá y vale 0 en
/// este corte porque lo esperado por documentos abiertos (RF-026) es fase 2.</summary>
public sealed class GetStockSummaryHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IStockReadQueries queries)
    : IQueryHandler<GetStockSummaryQuery, StockSummaryDto>
{
    public async Task<StockSummaryDto> HandleAsync(GetStockSummaryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await authorization.EnsureAsync(
            executionContext, query.TenantId, WmsPermissions.StockRead, query.OwnerId, query.WarehouseId, cancellationToken);

        var row = await queries.SummarizeAsync(query.TenantId, query.WarehouseId, query.OwnerId, query.VariantId, cancellationToken);

        return new StockSummaryDto(row.OnHand, row.Received, row.Available, row.Pickable, row.Reserved, row.Blocked, row.InTransit, Expected: 0m);
    }
}
