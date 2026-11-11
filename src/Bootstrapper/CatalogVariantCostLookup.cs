using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Infrastructure.Persistence;
using Modules.Pos.Application;

namespace Bootstrapper;

/// <summary>
/// El costo manual de las variantes de Catalog (spec 2026-11-10), leído para Pos. Una sola consulta
/// sobre <c>catalog.product_variant_costs</c> unida a <c>product_variants</c> por <c>tenant_id</c>:
/// la tabla de costos no lleva tenant, así que sin ese join una variante ajena respondería.
/// </summary>
internal sealed class CatalogVariantCostLookup(CatalogDbContext catalog) : IPosVariantCostLookup
{
    public async Task<IReadOnlyDictionary<Guid, decimal>> FindAsync(
        Guid tenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variantIds);
        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var ids = variantIds.Distinct().ToArray();
        var rows = await catalog.Database.SqlQuery<CostRow>($"""
            SELECT c.variant_id AS "VariantId", c.amount AS "Amount"
            FROM catalog.product_variant_costs c
            JOIN catalog.product_variants v ON v.id = c.variant_id
            WHERE v.tenant_id = {tenantId} AND c.currency = {currency} AND c.variant_id = ANY({ids})
            """).ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.VariantId, row => row.Amount);
    }

    private sealed record CostRow(Guid VariantId, decimal Amount);
}
