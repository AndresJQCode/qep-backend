using Modules.Suppliers.Domain;

namespace Modules.Suppliers.Application;

/// <summary>
/// Todo método recibe el <c>tenantId</c>: la defensa en profundidad del aislamiento no puede
/// depender de que el llamador se acuerde de filtrar.
/// </summary>
public interface ISupplierRepository
{
    Task<Supplier?> GetAsync(Guid tenantId, SupplierId supplierId, CancellationToken cancellationToken);

    void Add(Supplier supplier);

    /// <summary>Spec 2026-11-17 (mayorista): el proveedor de este tenant vinculado a <paramref name="linkedTenantId"/>, con
    /// tracking (puede escribirse a continuación).</summary>
    Task<Supplier?> FindByLinkedTenantAsync(Guid tenantId, Guid linkedTenantId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-17 (mayorista), D-8: el proveedor de este tenant con ese documento, con tracking.
    /// <paramref name="number"/> llega recortado, como lo guarda <see cref="SupplierIdentification"/>.</summary>
    Task<Supplier?> FindByIdentificationAsync(
        Guid tenantId,
        IdentificationType type,
        string number,
        CancellationToken cancellationToken);

    Task<(IReadOnlyList<Supplier> Items, int Total)> SearchAsync(
        Guid tenantId,
        string? search,
        SupplierStatusFilter? status,
        string? category,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Las categorías distintas del tenant, ordenadas, con tope <paramref name="take"/>.</summary>
    Task<IReadOnlyList<string>> ListCategoriesAsync(
        Guid tenantId,
        int take,
        CancellationToken cancellationToken);

    /// <summary>Los mismos filtros de la lista, pero por ventana (<paramref name="skip"/>/<paramref name="take"/>) y no por página, para la exportación.</summary>
    Task<IReadOnlyList<Supplier>> ListForExportAsync(
        Guid tenantId,
        string? search,
        SupplierStatusFilter? status,
        string? category,
        int skip,
        int take,
        CancellationToken cancellationToken);
}
