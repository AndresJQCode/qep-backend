using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>
/// SQL por nombre sobre tablas propias de Quotations: lo corre el borrado de producto dentro de la transacción
/// de Catalog (spec 2026-11-08), con el mismo <see cref="QuotationsDbContext"/> que el resto del request.
/// </summary>
internal sealed class DeletedVariantLineStore(QuotationsDbContext dbContext) : IDeletedVariantLineStore
{
    public async Task<int> BackfillProductSnapshotsAsync(
        Guid tenantId,
        IReadOnlyDictionary<Guid, QuotationProductSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        if (snapshots.Count == 0)
        {
            return 0;
        }

        var variantIds = snapshots.Keys.ToArray();
        var codes = snapshots.Values.Select(snapshot => snapshot.Code).ToArray();
        var names = snapshots.Values.Select(snapshot => snapshot.Name).ToArray();

        // Las cotizaciones fuera del borrador congelan código y nombre al salir de él; las enviadas antes del
        // 2026-09-26 no los tienen y se leían en vivo del catálogo. Al borrar la variante ya no hay de dónde
        // leerlos, así que se completan ahora. Un borrador no se toca: pierde la línea en otro paso.
        var quotationLines = await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE quotations.quotation_items qi
            SET product_code = s.code, product_name = s.name
            FROM quotations.quotations q,
                 unnest({variantIds}::uuid[], {codes}::text[], {names}::text[]) AS s(variant_id, code, name)
            WHERE q.id = qi.quotation_id AND q.tenant_id = {tenantId} AND q.status <> 'Draft'
              AND qi.variant_id = s.variant_id AND qi.product_code IS NULL;
            """,
            cancellationToken);

        var orderLines = await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE quotations.order_items oi
            SET product_code = s.code, product_name = s.name
            FROM quotations.orders o,
                 unnest({variantIds}::uuid[], {codes}::text[], {names}::text[]) AS s(variant_id, code, name)
            WHERE o.id = oi.order_id AND o.tenant_id = {tenantId}
              AND oi.variant_id = s.variant_id AND oi.product_code IS NULL;
            """,
            cancellationToken);

        return quotationLines + orderLines;
    }

    public async Task<IReadOnlyList<Quotation>> ListDraftsWithVariantsAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken)
    {
        var ids = variantIds.ToArray();
        return await dbContext.Quotations
            .Include(quotation => quotation.Items)
            .Include(quotation => quotation.Parties)
            .AsSingleQuery()
            .Where(quotation => quotation.TenantId == tenantId
                && quotation.Status == QuotationStatus.Draft
                && quotation.Items.Any(item => ids.Contains(item.VariantId)))
            .ToListAsync(cancellationToken);
    }
}
