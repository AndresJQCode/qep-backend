namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>
/// El SQL del ledger que EF no genera: la tabla particionada, el trigger de inmutabilidad, la función de particiones, la cola de
/// cambios de saldo y las dos vistas. Todo el SQL del <c>Up</c> es idempotente (<c>IF NOT EXISTS</c>, <c>OR REPLACE</c>) para que un
/// <c>Up</c> interrumpido se pueda repetir; las vistas y las FKs se crean al final, cuando EF ya creó las tablas.
/// <b>Estas constantes son historia de AddInventoryLedger:</b> un cambio posterior va en una migración nueva con su propio SQL, no
/// editando aquí, o una base nueva quedaría distinta de una ya migrada.
/// </summary>
internal static class InventoryLedgerSql
{
    public const string CreateStockMovements = """
        CREATE TABLE IF NOT EXISTS wms.stock_movements (
            id uuid NOT NULL, tenant_id uuid NOT NULL, owner_id uuid NOT NULL, variant_id uuid NOT NULL,
            lot_id uuid NULL, serial_unit_id uuid NULL,
            from_location_id uuid NOT NULL, from_status text NOT NULL, to_location_id uuid NOT NULL, to_status text NOT NULL,
            quantity numeric(18,6) NOT NULL CONSTRAINT "CK_stock_movements_quantity" CHECK (quantity > 0),
            captured_uom text NULL, captured_qty numeric(18,6) NULL, conversion_factor numeric(18,6) NULL,
            reason text NOT NULL, document_type text NULL, document_id uuid NULL, document_line_id uuid NULL,
            batch_id uuid NOT NULL, idempotency_key text NOT NULL,
            actor_id uuid NOT NULL, actor_type text NOT NULL, device_id text NULL,
            occurred_at timestamptz NOT NULL, recorded_at timestamptz NOT NULL,
            reverses_movement_id uuid NULL, reverses_occurred_at timestamptz NULL, override_reason text NULL,
            CONSTRAINT "PK_stock_movements" PRIMARY KEY (id, occurred_at),
            CONSTRAINT "CK_stock_movements_reversal_ref" CHECK ((reverses_movement_id IS NULL) = (reverses_occurred_at IS NULL))
        ) PARTITION BY RANGE (occurred_at);
        -- Sin FK hacia reverses_movement_id: PostgreSQL no admite FK a una tabla particionada salvo por la clave completa (S4).
        CREATE TABLE IF NOT EXISTS wms.stock_movements_default PARTITION OF wms.stock_movements DEFAULT;
        CREATE INDEX IF NOT EXISTS "IX_stock_movements_tenant_variant_occurred" ON wms.stock_movements (tenant_id, variant_id, occurred_at DESC);
        CREATE INDEX IF NOT EXISTS "IX_stock_movements_tenant_occurred" ON wms.stock_movements (tenant_id, occurred_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS "IX_stock_movements_tenant_document" ON wms.stock_movements (tenant_id, document_type, document_id);
        CREATE INDEX IF NOT EXISTS "IX_stock_movements_from_location" ON wms.stock_movements (from_location_id, occurred_at);
        CREATE INDEX IF NOT EXISTS "IX_stock_movements_to_location" ON wms.stock_movements (to_location_id, occurred_at);
        CREATE INDEX IF NOT EXISTS "IX_stock_movements_batch" ON wms.stock_movements (batch_id);
        CREATE INDEX IF NOT EXISTS "IX_stock_movements_reverses" ON wms.stock_movements (reverses_movement_id) WHERE reverses_movement_id IS NOT NULL;
        """;

    public const string CreateImmutabilityTrigger = """
        CREATE OR REPLACE FUNCTION wms.reject_ledger_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'wms.ledger.immutable' USING HINT = 'Stock movements are append-only; post a Reversal instead.'; END $$;
        DROP TRIGGER IF EXISTS trg_stock_movements_immutable ON wms.stock_movements;
        CREATE TRIGGER trg_stock_movements_immutable BEFORE UPDATE OR DELETE ON wms.stock_movements
            FOR EACH ROW EXECUTE FUNCTION wms.reject_ledger_mutation();
        """;

    // Límites en UTC (S26): occurred_at es timestamptz y un límite en la zona de la sesión partiría un mes en dos.
    public const string CreateEnsurePartitionFunction = """
        CREATE OR REPLACE FUNCTION wms.ensure_movement_partition(month date) RETURNS void LANGUAGE plpgsql AS $$
        DECLARE first_day date := date_trunc('month', month)::date;
                part_name text := format('stock_movements_y%sm%s', to_char(date_trunc('month', month), 'YYYY'), to_char(date_trunc('month', month), 'MM'));
        BEGIN
          IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'wms' AND c.relname = part_name) THEN
            EXECUTE format('CREATE TABLE wms.%I PARTITION OF wms.stock_movements FOR VALUES FROM (%L) TO (%L)',
                           part_name,
                           (first_day::timestamp AT TIME ZONE 'UTC'),
                           ((first_day + interval '1 month')::timestamp AT TIME ZONE 'UTC'));
          END IF;
        END $$;
        SELECT wms.ensure_movement_partition((now() AT TIME ZONE 'UTC')::date);
        SELECT wms.ensure_movement_partition(((now() AT TIME ZONE 'UTC') + interval '1 month')::date);
        """;

    public const string CreateBalanceChangeQueue = """
        CREATE TABLE IF NOT EXISTS wms.balance_change_queue (
            tenant_id uuid NOT NULL, owner_id uuid NOT NULL, warehouse_id uuid NOT NULL, variant_id uuid NOT NULL,
            queued_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT "PK_balance_change_queue" PRIMARY KEY (tenant_id, owner_id, warehouse_id, variant_id));
        """;

    // La única definición de `available` y `pickable`: is_virtual y allows_picking salen del join a locations (S1).
    public const string CreateStockRowsView = """
        CREATE OR REPLACE VIEW wms.v_stock_rows AS
        SELECT b.id, b.tenant_id, b.owner_id, b.warehouse_id, b.location_id, b.variant_id, b.lot_id, b.status, b.on_hand, b.reserved,
               CASE WHEN b.status = 'Available' AND NOT l.is_virtual THEN b.on_hand - b.reserved ELSE 0 END AS available,
               CASE WHEN b.status = 'Available' AND NOT l.is_virtual AND l.allows_picking THEN b.on_hand - b.reserved ELSE 0 END AS pickable,
               l.is_virtual, l.allows_picking, l.role AS location_role, l.code AS location_code, l.path AS location_path,
               b.allows_negative, b.version, b.updated_at
          FROM wms.stock_balances b JOIN wms.locations l ON l.id = b.location_id;
        """;

    // Las columnas exactas de S10.
    public const string CreateStockSummaryView = """
        CREATE OR REPLACE VIEW wms.v_stock_summary AS
        SELECT r.tenant_id, r.owner_id, r.warehouse_id, r.variant_id,
               sum(CASE WHEN NOT r.is_virtual THEN r.on_hand ELSE 0 END)                                                        AS on_hand,
               sum(CASE WHEN NOT r.is_virtual AND r.status = 'Received' THEN r.on_hand ELSE 0 END)                              AS received,
               sum(r.available)                                                                                                 AS available,
               sum(r.pickable)                                                                                                  AS pickable,
               sum(CASE WHEN NOT r.is_virtual THEN r.reserved ELSE 0 END)                                                       AS reserved,
               sum(CASE WHEN NOT r.is_virtual AND r.status IN ('Blocked', 'Quarantine', 'Damaged') THEN r.on_hand ELSE 0 END)   AS blocked,
               sum(CASE WHEN r.location_role = 'Transit' THEN r.on_hand ELSE 0 END)                                             AS in_transit
          FROM wms.v_stock_rows r
         GROUP BY r.tenant_id, r.owner_id, r.warehouse_id, r.variant_id;
        """;

    // Inventario básico §5.4: el resumen suma las reservas de demanda abiertas (sin ubicación), para que una variante reservada antes de
    // recibir nada aparezca con on_hand = 0 y reserved > 0. `available` = lo físico disponible menos lo reservado por demanda, negativo si
    // toca, sin recortar; `pickable` sigue siendo lo físico que se puede tomar y puede quedar por encima. Mismas siete columnas, mismo
    // orden y mismos tipos que CreateStockSummaryView (CREATE OR REPLACE no admite otra cosa). La definición anterior se conserva
    // porque AddInventoryLedger la usa y una migración ya aplicada no se reescribe.
    public const string CreateStockSummaryViewWithDemandReservations = """
        CREATE OR REPLACE VIEW wms.v_stock_summary AS
        WITH physical AS (
            SELECT r.tenant_id, r.owner_id, r.warehouse_id, r.variant_id,
                   sum(CASE WHEN NOT r.is_virtual THEN r.on_hand ELSE 0 END)                                                        AS on_hand,
                   sum(CASE WHEN NOT r.is_virtual AND r.status = 'Received' THEN r.on_hand ELSE 0 END)                              AS received,
                   sum(r.available)                                                                                                 AS available,
                   sum(r.pickable)                                                                                                  AS pickable,
                   sum(CASE WHEN NOT r.is_virtual THEN r.reserved ELSE 0 END)                                                       AS reserved,
                   sum(CASE WHEN NOT r.is_virtual AND r.status IN ('Blocked', 'Quarantine', 'Damaged') THEN r.on_hand ELSE 0 END)   AS blocked,
                   sum(CASE WHEN r.location_role = 'Transit' THEN r.on_hand ELSE 0 END)                                             AS in_transit
              FROM wms.v_stock_rows r
             GROUP BY r.tenant_id, r.owner_id, r.warehouse_id, r.variant_id),
        demand AS (
            SELECT d.tenant_id, d.owner_id, d.warehouse_id, d.variant_id, sum(d.quantity) AS reserved
              FROM wms.reservations d
             WHERE d.status = 'Open' AND d.location_id IS NULL
             GROUP BY d.tenant_id, d.owner_id, d.warehouse_id, d.variant_id),
        keys AS (
            SELECT tenant_id, owner_id, warehouse_id, variant_id FROM physical
            UNION
            SELECT tenant_id, owner_id, warehouse_id, variant_id FROM demand)
        SELECT k.tenant_id, k.owner_id, k.warehouse_id, k.variant_id,
               coalesce(p.on_hand, 0)                                AS on_hand,
               coalesce(p.received, 0)                               AS received,
               coalesce(p.available, 0) - coalesce(d.reserved, 0)    AS available,
               coalesce(p.pickable, 0)                               AS pickable,
               coalesce(p.reserved, 0) + coalesce(d.reserved, 0)     AS reserved,
               coalesce(p.blocked, 0)                                AS blocked,
               coalesce(p.in_transit, 0)                             AS in_transit
          FROM keys k
          LEFT JOIN physical p ON (p.tenant_id, p.owner_id, p.warehouse_id, p.variant_id) = (k.tenant_id, k.owner_id, k.warehouse_id, k.variant_id)
          LEFT JOIN demand d ON (d.tenant_id, d.owner_id, d.warehouse_id, d.variant_id) = (k.tenant_id, k.owner_id, k.warehouse_id, k.variant_id);
        """;

    // Spec §11.2: las FKs internas son ON DELETE RESTRICT (una ubicación con movimientos no se borra, se desactiva). Salen de una
    // tabla particionada hacia tablas normales, que PostgreSQL permite; ninguna apunta a stock_movements. Sin FKs a otros esquemas.
    public const string CreateInternalForeignKeys = """
        ALTER TABLE wms.stock_movements
            ADD CONSTRAINT "FK_stock_movements_from_location" FOREIGN KEY (from_location_id) REFERENCES wms.locations (id) ON DELETE RESTRICT,
            ADD CONSTRAINT "FK_stock_movements_to_location" FOREIGN KEY (to_location_id) REFERENCES wms.locations (id) ON DELETE RESTRICT,
            ADD CONSTRAINT "FK_stock_movements_owner" FOREIGN KEY (owner_id) REFERENCES wms.owners (id) ON DELETE RESTRICT,
            ADD CONSTRAINT "FK_stock_movements_lot" FOREIGN KEY (lot_id) REFERENCES wms.lots (id) ON DELETE RESTRICT,
            ADD CONSTRAINT "FK_stock_movements_serial_unit" FOREIGN KEY (serial_unit_id) REFERENCES wms.serial_units (id) ON DELETE RESTRICT;
        ALTER TABLE wms.stock_balances
            ADD CONSTRAINT "FK_stock_balances_location" FOREIGN KEY (location_id) REFERENCES wms.locations (id) ON DELETE RESTRICT,
            ADD CONSTRAINT "FK_stock_balances_owner" FOREIGN KEY (owner_id) REFERENCES wms.owners (id) ON DELETE RESTRICT,
            ADD CONSTRAINT "FK_stock_balances_warehouse" FOREIGN KEY (warehouse_id) REFERENCES wms.warehouses (id) ON DELETE RESTRICT,
            ADD CONSTRAINT "FK_stock_balances_lot" FOREIGN KEY (lot_id) REFERENCES wms.lots (id) ON DELETE RESTRICT;
        ALTER TABLE wms.lots
            ADD CONSTRAINT "FK_lots_owner" FOREIGN KEY (owner_id) REFERENCES wms.owners (id) ON DELETE RESTRICT;
        ALTER TABLE wms.serial_units
            ADD CONSTRAINT "FK_serial_units_owner" FOREIGN KEY (owner_id) REFERENCES wms.owners (id) ON DELETE RESTRICT,
            ADD CONSTRAINT "FK_serial_units_location" FOREIGN KEY (current_location_id) REFERENCES wms.locations (id) ON DELETE RESTRICT,
            ADD CONSTRAINT "FK_serial_units_lot" FOREIGN KEY (lot_id) REFERENCES wms.lots (id) ON DELETE RESTRICT;
        ALTER TABLE wms.reservations
            ADD CONSTRAINT "FK_reservations_owner" FOREIGN KEY (owner_id) REFERENCES wms.owners (id) ON DELETE RESTRICT,
            ADD CONSTRAINT "FK_reservations_location" FOREIGN KEY (location_id) REFERENCES wms.locations (id) ON DELETE RESTRICT,
            ADD CONSTRAINT "FK_reservations_warehouse" FOREIGN KEY (warehouse_id) REFERENCES wms.warehouses (id) ON DELETE RESTRICT,
            ADD CONSTRAINT "FK_reservations_lot" FOREIGN KEY (lot_id) REFERENCES wms.lots (id) ON DELETE RESTRICT;
        ALTER TABLE wms.adjustments
            ADD CONSTRAINT "FK_adjustments_owner" FOREIGN KEY (owner_id) REFERENCES wms.owners (id) ON DELETE RESTRICT,
            ADD CONSTRAINT "FK_adjustments_warehouse" FOREIGN KEY (warehouse_id) REFERENCES wms.warehouses (id) ON DELETE RESTRICT;
        ALTER TABLE wms.adjustment_lines
            ADD CONSTRAINT "FK_adjustment_lines_location" FOREIGN KEY (location_id) REFERENCES wms.locations (id) ON DELETE RESTRICT,
            ADD CONSTRAINT "FK_adjustment_lines_lot" FOREIGN KEY (lot_id) REFERENCES wms.lots (id) ON DELETE RESTRICT;
        """;

    public static string EnsurePartition(DateOnly month) => $"SELECT wms.ensure_movement_partition('{month:yyyy-MM-01}')";
}
