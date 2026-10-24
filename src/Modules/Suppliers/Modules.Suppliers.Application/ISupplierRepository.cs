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
