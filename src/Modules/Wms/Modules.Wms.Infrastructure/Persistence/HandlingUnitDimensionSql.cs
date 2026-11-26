namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>
/// D-12: el SQL de <c>AddHandlingUnitDimension</c> que EF no genera, porque <c>stock_movements</c> está fuera de sus migraciones y
/// las vistas son SQL crudo. Idempotente, como <see cref="InventoryLedgerSql"/>. <b>Estas constantes son historia de
/// AddHandlingUnitDimension:</b> un cambio posterior va en una migración nueva, no editando aquí. Sin FKs: <c>wms.handling_units</c>
/// no existe todavía, y las agrega el corte que la crea (las columnas están en <c>NULL</c>, así que validarlas es inmediato).
/// </summary>
internal static class HandlingUnitDimensionSql
{
    // ADD COLUMN sobre la tabla particionada se propaga a todas sus particiones, también a DEFAULT, y a las que cree después
    // wms.ensure_movement_partition. El trigger de inmutabilidad es por fila y no se dispara con un ALTER.
    public const string AddMovementColumns = """
        ALTER TABLE wms.stock_movements
            ADD COLUMN IF NOT EXISTS from_handling_unit_id uuid NULL,
            ADD COLUMN IF NOT EXISTS to_handling_unit_id uuid NULL;
        """;

    // La misma vista de InventoryLedgerSql.CreateStockRowsView con handling_unit_id al final: un OR REPLACE sólo deja agregar columnas
    // al final, y v_stock_summary depende de esta vista. El resumen no cambia: agrupa por variante.
    public const string ReplaceStockRowsView = """
        CREATE OR REPLACE VIEW wms.v_stock_rows AS
        SELECT b.id, b.tenant_id, b.owner_id, b.warehouse_id, b.location_id, b.variant_id, b.lot_id, b.status, b.on_hand, b.reserved,
               CASE WHEN b.status = 'Available' AND NOT l.is_virtual THEN b.on_hand - b.reserved ELSE 0 END AS available,
               CASE WHEN b.status = 'Available' AND NOT l.is_virtual AND l.allows_picking THEN b.on_hand - b.reserved ELSE 0 END AS pickable,
               l.is_virtual, l.allows_picking, l.role AS location_role, l.code AS location_code, l.path AS location_path,
               b.allows_negative, b.version, b.updated_at, b.handling_unit_id
          FROM wms.stock_balances b JOIN wms.locations l ON l.id = b.location_id;
        """;
}
