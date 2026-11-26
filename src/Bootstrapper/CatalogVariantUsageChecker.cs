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
/// **Acoplamiento con Quotations y Pos.** La columna <c>variant_id</c> de las tres tablas de
/// líneas y sus índices (<c>IX_quotation_items_variant</c>, <c>IX_order_items_variant</c>,
/// <c>IX_sale_lines_variant</c>) los crean las migraciones <c>AddVariantToLines</c> (Quotations) y
/// <c>AddVariantToSaleLines</c> (Pos). Renombrar una columna allá compila y rompe esta consulta en
/// ejecución con <c>42703 undefined_column</c>: las pruebas gemelas contra el esquema migrado son
/// <c>VariantLinesMigrationTests</c> y <c>SaleLinesVariantMigrationTests</c>
/// (<c>TheCatalogUsageQueryFindsTheBackfilledVariant</c>).
///
/// **El ledger de WMS también cuenta como uso** (spec WMS 2026-10-31 §17): <c>wms.stock_movements</c> y <c>wms.stock_balances</c>
/// llevan <c>tenant_id</c> y <c>variant_id</c> propios, sin join. Una variante con un solo movimiento —aunque su saldo haya vuelto a
/// cero— no se borra, se desactiva: el historial del ledger es inmutable y no puede quedar apuntando a una variante que no existe.
/// Renombrar esas columnas compila y rompe esta consulta con <c>42703</c>; la amarran <c>CatalogVariantUsageCheckerTests</c> (réplica
/// a mano) y <c>StockProbeTests.TheRealCheckerSeesAWmsMovementAgainstTheMigratedSchema</c> (esquema migrado).
///
/// **También cuentan lo que todavía puede escribir en el ledger** (revisión final de WMS-D): las líneas de un ajuste abierto
/// (<c>wms.adjustment_lines</c> unida a <c>wms.adjustments</c> por el tenant, con estado <c>Proposed</c> o <c>Approved</c>, que se guardan
/// como texto) y los lotes (<c>wms.lots</c>). Un ajuste propuesto sobre una variante sin movimientos no la protegía: Catalog la borraba y
/// el posteo escribía después un movimiento inmutable sobre una variante inexistente. <c>Posted</c> ya lo cubre su movimiento y
/// <c>Rejected</c> no escribe nada. Amarra el esquema migrado <c>StockProbeTests.TheRealCheckerSeesAnOpenAdjustmentLineAndALotAgainstTheMigratedSchema</c>.
///
/// Por lo mismo cuentan las líneas abiertas de las tareas de bodega sin terminar (spec 2026-11-26, DA-37):
/// <c>wms.task_lines.variant_id</c> con <c>line_status = 'Open'</c>, unida a <c>wms.tasks</c> por el tenant, con estado
/// <c>Planned</c>, <c>Ready</c>, <c>Assigned</c> o <c>InProgress</c> (como texto). Un estado de tarea nuevo que todavía pueda
/// confirmar entra aquí. Amarra el esquema migrado <c>StockProbeTests.TheRealCheckerSeesAnOpenTaskLineAgainstTheMigratedSchema</c>.
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

        // UNION ya deduplica: es el SELECT DISTINCT de todas las tablas en una sola ida.
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
            UNION
            SELECT m.variant_id
            FROM wms.stock_movements m
            WHERE m.tenant_id = {tenantId} AND m.variant_id = ANY({ids})
            UNION
            SELECT b.variant_id
            FROM wms.stock_balances b
            WHERE b.tenant_id = {tenantId} AND b.variant_id = ANY({ids})
            UNION
            SELECT al.variant_id
            FROM wms.adjustment_lines al
            JOIN wms.adjustments a ON a.id = al.adjustment_id
            WHERE a.tenant_id = {tenantId} AND a.status IN ('Proposed', 'Approved') AND al.variant_id = ANY({ids})
            UNION
            SELECT l.variant_id
            FROM wms.lots l
            WHERE l.tenant_id = {tenantId} AND l.variant_id = ANY({ids})
            UNION
            SELECT tl.variant_id
            FROM wms.task_lines tl
            JOIN wms.tasks t ON t.id = tl.task_id
            WHERE t.tenant_id = {tenantId} AND t.status IN ('Planned', 'Ready', 'Assigned', 'InProgress')
              AND tl.line_status = 'Open' AND tl.variant_id = ANY({ids})
            """).ToListAsync(cancellationToken);

        return used.ToHashSet();
    }
}
