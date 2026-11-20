using BuildingBlocks.Application;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

public sealed record ListSupplierCatalogQuery(Guid TenantId, Guid SupplierId, string? Search, int Page, int PageSize)
    : IQuery<SupplierCatalogPageDto>;

/// <summary>
/// Una variante del catálogo del mayorista para armar la orden. Precios con IVA (base, descuento y lista) para pintar «base y
/// descuento», y <c>UnitCost</c> = lo que la orden guardaría, la lista sin IVA (D-9): la pantalla no divide.
/// </summary>
public sealed record SupplierCatalogItemDto(
    Guid VariantId,
    string Code,
    string Name,
    decimal BasePrice,
    decimal DiscountPercent,
    decimal ListPrice,
    decimal TaxRatePercent,
    decimal UnitCost,
    Guid ProductId = default,
    string ProductName = "",
    string? ImageUrl = null);

/// <summary>La lista de precios que aplica al distribuidor (adenda: la tarjeta «Tarifa: Distribuidor estándar»).</summary>
public sealed record SupplierCatalogPriceListDto(string Name, decimal DiscountPercent);

/// <summary>
/// Una página del catálogo del mayorista en su moneda por defecto (la que tendrá la orden, D-11), con la lista que la precia.
/// <c>Total</c> cuenta productos: la página es de productos con sus variantes vendibles.
/// </summary>
public sealed record SupplierCatalogPageDto(
    string Currency,
    IReadOnlyList<SupplierCatalogItemDto> Items,
    int Total,
    int Page,
    int PageSize,
    SupplierCatalogPriceListDto PriceList);

/// <summary>
/// Mayorista fase 3: el catálogo del mayorista de un proveedor vinculado. Exige <c>purchases.order.manage</c> (es para armar una
/// orden). Proveedor que no es del tenant: 404 <c>suppliers.supplier.not_found</c>; no vinculado: 422
/// <c>purchases.order.supplier_not_linked</c>; relación no activa: 403 <c>wholesale.relationship.inactive</c> desde el
/// adaptador. El mayorista sale del proveedor guardado, nunca del pedido.
/// </summary>
public sealed class ListSupplierCatalogHandler(
    IExecutionContext executionContext,
    IPurchasesSupplierLookup suppliers,
    IWholesaleCatalog wholesaleCatalog)
    : IQueryHandler<ListSupplierCatalogQuery, SupplierCatalogPageDto>
{
    internal const int DefaultPageSize = 50;

    internal const int MaxPageSize = 200;

    public async Task<SupplierCatalogPageDto> HandleAsync(ListSupplierCatalogQuery query, CancellationToken cancellationToken)
    {
        PurchasesAuthorization.EnsureAuthorized(executionContext, query.TenantId, PurchasesPermissions.OrderManage);
        var supplier = await suppliers.FindAsync(query.TenantId, query.SupplierId, cancellationToken)
            ?? throw PurchasesConflicts.SupplierNotFound(query.SupplierId);
        var wholesalerTenantId = supplier.LinkedTenantId
            ?? throw new PurchasesDomainException(
                PurchasesErrorCodes.SupplierNotLinked, "The supplier is not a wholesaler in QEP.");

        var page = Math.Max(1, query.Page);
        var pageSize = query.PageSize < 1 ? DefaultPageSize : Math.Min(query.PageSize, MaxPageSize);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var result = await wholesaleCatalog.SearchAsync(
            query.TenantId, wholesalerTenantId, search, page, pageSize, cancellationToken);

        return new SupplierCatalogPageDto(
            result.Currency,
            result.Items
                .Select(item => new SupplierCatalogItemDto(
                    item.VariantId,
                    item.Code,
                    item.Name,
                    item.BasePrice,
                    item.DiscountPercent,
                    item.ListPrice,
                    item.TaxRatePercent,
                    PurchaseOrderMath.NetOfVat(item.ListPrice, item.TaxRatePercent),
                    item.ProductId,
                    item.ProductName,
                    item.ImageUrl))
                .ToList(),
            result.Total,
            page,
            pageSize,
            new SupplierCatalogPriceListDto(result.PriceListName, result.DiscountPercent));
    }
}
