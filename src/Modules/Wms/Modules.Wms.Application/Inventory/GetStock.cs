using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

public sealed record GetStockQuery(
    Guid TenantId,
    Guid? WarehouseId,
    Guid? OwnerId,
    Guid? VariantId,
    Guid? LocationId,
    Guid? LotId,
    string? Status,
    bool OnlyAvailable,
    bool IncludeVirtual,
    string? Search,
    int Page,
    int PageSize) : IQuery<StockPageDto>;

/// <summary>
/// Los saldos filtrados y paginados (RF-021). Lee sólo <c>stock_balances</c> por la vista, sin tocar el ledger. El producto, su
/// código y el código de barras salen de Catalog y la unidad base del perfil, y se pegan acá: <c>Application</c> de este módulo no ve
/// otros módulos y la vista no puede unirlos. <c>search</c> casa sólo contra el código de la ubicación y el del lote en este corte:
/// el buscador de producto de la pantalla manda <c>variantId</c>, que ya viene resuelto.
/// </summary>
public sealed class GetStockHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IStockReadQueries queries,
    IWmsCatalogReader catalog,
    IVariantProfileRepository profiles)
    : IQueryHandler<GetStockQuery, StockPageDto>
{
    public async Task<StockPageDto> HandleAsync(GetStockQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await authorization.EnsureAsync(
            executionContext, query.TenantId, WmsPermissions.StockRead, query.OwnerId, query.WarehouseId, cancellationToken);

        var page = WmsPaging.NormalizePage(query.Page);
        var pageSize = WmsPaging.NormalizePageSize(query.PageSize);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var filter = new StockFilter(
            query.TenantId,
            query.WarehouseId,
            query.OwnerId,
            query.VariantId,
            query.LocationId,
            query.LotId,
            StockStatuses.ParseOrNull(query.Status),
            query.OnlyAvailable,
            query.IncludeVirtual,
            search,
            (long)(page - 1) * pageSize,
            pageSize);

        var result = await queries.SearchAsync(filter, cancellationToken);

        var variantIds = result.Rows.Select(row => row.VariantId).Distinct().ToList();
        var snapshots = (await catalog.GetVariantsAsync(query.TenantId, variantIds, cancellationToken)).ToDictionary(snapshot => snapshot.VariantId);
        var variantProfiles = (await profiles.ListAsync(query.TenantId, variantIds, cancellationToken)).ToDictionary(profile => profile.VariantId);

        var items = result.Rows.Select(row => StockRowMapper.ToDto(
            row, snapshots.GetValueOrDefault(row.VariantId), variantProfiles.GetValueOrDefault(row.VariantId))).ToList();

        return new StockPageDto(items, page, pageSize, result.Total, result.Totals);
    }
}

/// <summary>Una fila de la vista de saldos tal como la pinta la pantalla: la comparten la lista y la exportación, para que el Excel diga
/// lo mismo que la grilla.</summary>
internal static class StockRowMapper
{
    public static StockRowDto ToDto(StockBalanceRow row, WmsVariantSnapshot? variant, VariantLogisticsProfile? profile) =>
        new(
            row.OwnerId,
            row.OwnerName,
            row.WarehouseId,
            row.WarehouseCode,
            row.LocationId,
            row.LocationCode,
            row.LocationPath,
            row.VariantId,
            variant?.ProductName ?? string.Empty,
            variant?.Code ?? string.Empty,
            variant?.Barcode,
            row.LotId,
            row.LotCode,
            row.ExpirationDate,
            row.Status,
            row.OnHand,
            row.Reserved,
            row.Available,
            row.Pickable,
            profile?.BaseUom.Value ?? WmsDefaults.BaseUom,
            profile?.DisplayUom?.Value,
            DisplayQty(row.OnHand, profile),
            row.UpdatedAt);

    /// <summary>Convierte a la presentación preferida sólo si el perfil la declara como presentación (o es la base): un
    /// <c>display_uom</c> que ya no coincide con ninguna conversión no inventa un factor.</summary>
    private static decimal? DisplayQty(decimal onHand, VariantLogisticsProfile? profile)
    {
        if (profile?.DisplayUom is not { } display)
        {
            return null;
        }

        if (display == profile.BaseUom)
        {
            return onHand;
        }

        var conversion = profile.Conversions.FirstOrDefault(item => string.Equals(item.Code, display.Value, StringComparison.OrdinalIgnoreCase));
        return conversion is null ? null : onHand / conversion.Factor;
    }
}

internal static class WmsDefaults
{
    /// <summary>ADR-07: la unidad base del perfil por defecto. Sólo respalda a una fila cuyo perfil no se pudo leer.</summary>
    public const string BaseUom = "UND";
}

internal static class StockStatuses
{
    /// <summary><c>null</c> si no vino; <c>wms.stock.status_invalid</c> si no es un nombre exacto del enum (<c>Enum.TryParse</c> aceptaría
    /// «1» y cualquier mayúscula).</summary>
    public static StockStatus? ParseOrNull(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var name = status.Trim();
        return Enum.GetNames<StockStatus>().Contains(name, StringComparer.Ordinal)
            ? Enum.Parse<StockStatus>(name)
            : throw new WmsDomainException(
                WmsErrorCodes.StockStatusInvalid, $"The status must be one of: {string.Join(", ", Enum.GetNames<StockStatus>())}.");
    }
}
