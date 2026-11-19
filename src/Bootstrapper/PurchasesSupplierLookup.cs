using Microsoft.EntityFrameworkCore;
using Modules.Purchases.Application;
using Modules.Suppliers.Domain;
using Modules.Suppliers.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Suppliers → Purchases: el proveedor de una orden es un id suelto y Compras sólo lee nombre, documento, si está activo y, desde
/// la fase 3 de mayorista, el tenant con que está vinculado. Todo acotado al tenant: un id de otro tenant llega como «no existe».
/// </summary>
internal sealed class PurchasesSupplierLookup(SuppliersDbContext suppliers) : IPurchasesSupplierLookup
{
    // Mismo escape que SupplierRepository: un «%» o un «_» que escribe la persona se busca literal.
    private const string LikeEscapeCharacter = "\\";

    public async Task<PurchasesSupplierRef?> FindAsync(Guid tenantId, Guid supplierId, CancellationToken cancellationToken)
    {
        var found = await FindManyAsync(tenantId, [supplierId], cancellationToken);
        return found.GetValueOrDefault(supplierId);
    }

    public async Task<IReadOnlyDictionary<Guid, PurchasesSupplierRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken)
    {
        if (supplierIds.Count == 0)
        {
            return new Dictionary<Guid, PurchasesSupplierRef>();
        }

        var ids = supplierIds.Distinct().Select(id => new SupplierId(id)).ToArray();
        var rows = await suppliers.Suppliers.AsNoTracking()
            .Where(supplier => supplier.TenantId == tenantId && ids.Contains(supplier.Id))
            .Select(supplier => new
            {
                supplier.Id,
                supplier.Name,
                supplier.Identification.Type,
                supplier.Identification.Number,
                supplier.IsActive,
                supplier.LinkedTenantId,
                supplier.Contact.Email,
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.Id.Value,
            row => new PurchasesSupplierRef(
                row.Id.Value,
                row.Name,
                row.Type.ToWire(),
                row.Number,
                row.IsActive,
                row.LinkedTenantId,
                string.IsNullOrWhiteSpace(row.Email) ? null : row.Email.Trim()));
    }

    public async Task<IReadOnlyList<Guid>> SearchIdsAsync(Guid tenantId, string search, int take, CancellationToken cancellationToken)
    {
        var trimmed = search?.Trim();
        if (string.IsNullOrEmpty(trimmed) || take < 1)
        {
            return [];
        }

        var pattern = $"%{Escape(trimmed)}%";
        var ids = await suppliers.Suppliers.AsNoTracking()
            .Where(supplier => supplier.TenantId == tenantId
                && (EF.Functions.ILike(supplier.Name, pattern, LikeEscapeCharacter)
                    || (supplier.BusinessName != null
                        && EF.Functions.ILike(supplier.BusinessName, pattern, LikeEscapeCharacter))))
            .OrderBy(supplier => supplier.Name)
            .ThenBy(supplier => supplier.Id)
            .Select(supplier => supplier.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
        return ids.Select(id => id.Value).ToList();
    }

    // La barra va primero: escaparla después convertiría en literal la barra que acaban de agregar los otros dos reemplazos.
    private static string Escape(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
