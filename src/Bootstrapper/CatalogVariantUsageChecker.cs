using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Adapta las líneas de Quotations y Pos al puerto <see cref="IVariantUsageChecker"/> de Catalog
/// (spec 2026-10-20 §3.3). Vive en el composition root por la misma razón que
/// <see cref="PriceChangeReportSource"/>: Catalog no referencia los otros módulos
/// (<c>CatalogLayerTests</c>) y el acoplamiento entre esquemas se cablea acá.
///
/// **Corre sobre el <see cref="CatalogDbContext"/> de la petición, no sobre el de Quotations o
/// Pos.** Así la consulta usa la misma conexión que el PUT de producto y, si el handler abrió una
/// transacción (<see cref="ICatalogUnitOfWork.BeginTransactionAsync"/>), queda dentro de ella: es
/// la «comprobación que repite el borrado en la misma transacción» del spec. Con otro contexto
/// iría por otra conexión y la palabra «transacción» no significaría nada.
///
/// Las líneas no tienen <c>tenant_id</c> propio: el tenant se filtra por el documento dueño
/// (<c>quotations.quotations</c>, <c>quotations.orders</c>, <c>pos.sales</c>). Todo método que
/// toca otro esquema recibe el tenant, aunque los ids de variante ya sean únicos: un id de otro
/// tenant no puede decir «en uso» acá.
///
/// **Acoplamiento con las Tareas 10 y 12.** La columna <c>variant_id</c> de las tres tablas de
/// líneas la crean las migraciones <c>AddVariantToLines</c> (Quotations) y
/// <c>AddVariantToSaleLines</c> (Pos). Hasta que existan, esta consulta falla en ejecución con
/// <c>42703 undefined_column</c>. El SQL está escrito contra el esquema final del plan a propósito:
/// una versión intermedia por <c>product_id</c> diría «en uso» por la variante por defecto y no por
/// la que se vendió, y nadie la volvería a cambiar.
/// </summary>
internal sealed class CatalogVariantUsageChecker(CatalogDbContext catalog) : IVariantUsageChecker
{
    public async Task<IReadOnlySet<Guid>> FindUsedAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variantIds);
        if (variantIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = variantIds.Distinct().ToArray();

        // UNION ya deduplica: es el SELECT DISTINCT de las tres tablas en una sola ida.
        var used = await catalog.Database.SqlQuery<Guid>($"""
            SELECT qi.variant_id AS "Value"
            FROM quotations.quotation_items qi
            JOIN quotations.quotations q ON q.id = qi.quotation_id
            WHERE q.tenant_id = {tenantId} AND qi.variant_id = ANY({ids})
            UNION
            SELECT oi.variant_id
            FROM quotations.order_items oi
            JOIN quotations.orders o ON o.id = oi.order_id
            WHERE o.tenant_id = {tenantId} AND oi.variant_id = ANY({ids})
            UNION
            SELECT sl.variant_id
            FROM pos.sale_lines sl
            JOIN pos.sales s ON s.id = sl.sale_id
            WHERE s.tenant_id = {tenantId} AND sl.variant_id = ANY({ids})
            """).ToListAsync(cancellationToken);

        return used.ToHashSet();
    }
}
