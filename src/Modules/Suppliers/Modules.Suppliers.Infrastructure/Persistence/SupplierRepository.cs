using Microsoft.EntityFrameworkCore;
using Modules.Suppliers.Application;
using Modules.Suppliers.Domain;

namespace Modules.Suppliers.Infrastructure.Persistence;

internal sealed class SupplierRepository(SuppliersDbContext dbContext) : ISupplierRepository
{
    private const string LikeEscapeCharacter = "\\";

    // La barra va primero: escaparla después convertiría en literal la barra que acaban de agregar
    // los otros dos reemplazos.
    private static string EscapeLikeWildcards(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    // null para un filtro vacío/ausente: "%%" matchearía todo, que no es lo mismo que no filtrar.
    private static string? ContainsPattern(string? term)
    {
        var trimmed = term?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : $"%{EscapeLikeWildcards(trimmed)}%";
    }

    // Igualdad sin distinguir mayúsculas: ILIKE con el valor escapado y sin comodines.
    private static string? EqualsPattern(string? term)
    {
        var trimmed = term?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : EscapeLikeWildcards(trimmed);
    }

    // Los filtros del listado, aplicados una sola vez para los dos caminos que los usan (la página
    // y el recorrido de la exportación).
    private IQueryable<Supplier> FilteredQuery(
        Guid tenantId,
        string? search,
        SupplierStatusFilter? status,
        string? category)
    {
        var query = dbContext.Suppliers
            .AsNoTracking()
            .Where(supplier => supplier.TenantId == tenantId);

        var searchPattern = ContainsPattern(search);
        if (searchPattern is not null)
        {
            query = query.Where(supplier =>
                EF.Functions.ILike(supplier.Name, searchPattern, LikeEscapeCharacter) ||
                (supplier.BusinessName != null && EF.Functions.ILike(
                    supplier.BusinessName, searchPattern, LikeEscapeCharacter)) ||
                EF.Functions.ILike(
                    supplier.Identification.Number, searchPattern, LikeEscapeCharacter));
        }

        if (status is { } filter)
        {
            var wantActive = filter == SupplierStatusFilter.Active;
            query = query.Where(supplier => supplier.IsActive == wantActive);
        }

        var categoryPattern = EqualsPattern(category);
        if (categoryPattern is not null)
        {
            query = query.Where(supplier =>
                supplier.Category != null && EF.Functions.ILike(
                    supplier.Category, categoryPattern, LikeEscapeCharacter));
        }

        return query;
    }

    public Task<Supplier?> GetAsync(
        Guid tenantId,
        SupplierId supplierId,
        CancellationToken cancellationToken) =>
        dbContext.Suppliers.FirstOrDefaultAsync(
            supplier => supplier.TenantId == tenantId && supplier.Id == supplierId,
            cancellationToken);

    public void Add(Supplier supplier) => dbContext.Suppliers.Add(supplier);

    public async Task<(IReadOnlyList<Supplier> Items, int Total)> SearchAsync(
        Guid tenantId,
        string? search,
        SupplierStatusFilter? status,
        string? category,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = FilteredQuery(tenantId, search, status, category);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(supplier => supplier.Name)
            .ThenBy(supplier => supplier.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<string>> ListCategoriesAsync(
        Guid tenantId,
        int take,
        CancellationToken cancellationToken) =>
        await dbContext.Suppliers
            .AsNoTracking()
            .Where(supplier => supplier.TenantId == tenantId && supplier.Category != null)
            .Select(supplier => supplier.Category!)
            .Distinct()
            .OrderBy(category => category)
            .Take(take)
            .ToListAsync(cancellationToken);

    // Orden por Name y Id: recorrer por ventanas un orden que empata puede saltear o repetir filas
    // entre una consulta y la siguiente, y en un archivo exportado no lo ve nadie.
    public async Task<IReadOnlyList<Supplier>> ListForExportAsync(
        Guid tenantId,
        string? search,
        SupplierStatusFilter? status,
        string? category,
        int skip,
        int take,
        CancellationToken cancellationToken) =>
        await FilteredQuery(tenantId, search, status, category)
            .OrderBy(supplier => supplier.Name)
            .ThenBy(supplier => supplier.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
}
