using Microsoft.EntityFrameworkCore;
using Modules.Suppliers.Application;
using Modules.Wholesale.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Wholesale → Suppliers (mayorista fase 3, adenda del contrato): el estado de la relación entre el distribuidor (el tenant de la
/// ruta) y cada mayorista que tiene como proveedor vinculado, por nombre. Sólo las filas cuyo distribuidor es ese tenant: un
/// mayorista que no lo tiene como distribuidor no viene, y la lista lo pinta como <c>null</c>.
/// </summary>
internal sealed class SuppliersWholesaleRelationshipLookup(WholesaleDbContext wholesale) : ISupplierRelationshipLookup
{
    public async Task<IReadOnlyDictionary<Guid, string>> FindStatusesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> wholesalerTenantIds, CancellationToken cancellationToken)
    {
        if (wholesalerTenantIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = wholesalerTenantIds.Distinct().ToArray();
        // El par (mayorista, distribuidor) es único (WholesaleDbContext), así que hay a lo sumo una fila por mayorista.
        var rows = await wholesale.Distributors.AsNoTracking()
            .Where(distributor => distributor.DistributorTenantId == tenantId && ids.Contains(distributor.TenantId))
            .Select(distributor => new { distributor.TenantId, distributor.Status })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.TenantId, row => row.Status.ToString());
    }
}
