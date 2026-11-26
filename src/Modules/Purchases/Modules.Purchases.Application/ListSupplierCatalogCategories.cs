using BuildingBlocks.Application;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

public sealed record ListSupplierCatalogCategoriesQuery(Guid TenantId, Guid SupplierId) : IQuery<SupplierCatalogCategoriesDto>;

/// <summary>Una categoría del árbol que el mayorista le deja ver al comprador; <c>ParentId</c> nulo es raíz.</summary>
public sealed record SupplierCatalogCategoryDto(Guid Id, string Name, Guid? ParentId);

/// <summary>El árbol visible, plano y en el orden del mayorista (spec 2026-11-20): el frontend no ordena ni cuenta.</summary>
public sealed record SupplierCatalogCategoriesDto(IReadOnlyList<SupplierCatalogCategoryDto> Items);

/// <summary>
/// Las categorías del mayorista de un proveedor vinculado, recortadas a lo que la lista del distribuidor le deja ver. Mismas
/// guardas que <see cref="ListSupplierCatalogHandler"/>: <c>purchases.order.manage</c>, 404 <c>suppliers.supplier.not_found</c>,
/// 422 <c>purchases.order.supplier_not_linked</c> y 403 <c>wholesale.relationship.inactive</c> desde el adaptador.
/// </summary>
public sealed class ListSupplierCatalogCategoriesHandler(
    IExecutionContext executionContext,
    IPurchasesSupplierLookup suppliers,
    IWholesaleCatalog wholesaleCatalog)
    : IQueryHandler<ListSupplierCatalogCategoriesQuery, SupplierCatalogCategoriesDto>
{
    public async Task<SupplierCatalogCategoriesDto> HandleAsync(
        ListSupplierCatalogCategoriesQuery query, CancellationToken cancellationToken)
    {
        PurchasesAuthorization.EnsureAuthorized(executionContext, query.TenantId, PurchasesPermissions.OrderManage);
        var supplier = await suppliers.FindAsync(query.TenantId, query.SupplierId, cancellationToken)
            ?? throw PurchasesConflicts.SupplierNotFound(query.SupplierId);
        var wholesalerTenantId = supplier.LinkedTenantId
            ?? throw new PurchasesDomainException(
                PurchasesErrorCodes.SupplierNotLinked, "The supplier is not a wholesaler in QEP.");

        var categories = await wholesaleCatalog.ListCategoriesAsync(query.TenantId, wholesalerTenantId, cancellationToken);
        return new SupplierCatalogCategoriesDto(
            [.. categories.Select(category => new SupplierCatalogCategoryDto(category.Id, category.Name, category.ParentId))]);
    }
}
