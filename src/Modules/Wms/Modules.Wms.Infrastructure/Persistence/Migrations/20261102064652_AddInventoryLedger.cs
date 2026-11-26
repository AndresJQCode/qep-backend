using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El ledger, su trigger, las particiones y la cola son SQL crudo e idempotente: EF no sabe particionar. Va antes de las
            // tablas de EF; las vistas y las FKs internas, al final, cuando las tablas de EF ya existen.
            migrationBuilder.Sql(InventoryLedgerSql.CreateStockMovements);
            migrationBuilder.Sql(InventoryLedgerSql.CreateImmutabilityTrigger);
            migrationBuilder.Sql(InventoryLedgerSql.CreateEnsurePartitionFunction);
            migrationBuilder.Sql(InventoryLedgerSql.CreateBalanceChangeQueue);

            migrationBuilder.CreateTable(
                name: "adjustments",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    proposed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejected_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reject_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adjustments", x => x.id);
                    table.CheckConstraint("CK_adjustments_reason", "reason IN ('InitialLoad','CountVariance','Damage','Loss','Found','Correction','Other')");
                    table.CheckConstraint("CK_adjustments_status", "status IN ('Proposed','Approved','Posted','Rejected')");
                });

            migrationBuilder.CreateTable(
                name: "balance_checks",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ledger_on_hand = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    balance_on_hand = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    last_movement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_balance_checks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "import_previews",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rows_json = table.Column<string>(type: "jsonb", nullable: false),
                    can_commit = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_previews", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "lots",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expiration_date = table.Column<DateOnly>(type: "date", nullable: true),
                    manufactured_at = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lots", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "operation_receipts",
                schema: "wms",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_hash = table.Column<string>(type: "char(64)", nullable: false),
                    response_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operation_receipts", x => new { x.tenant_id, x.idempotency_key });
                });

            migrationBuilder.CreateTable(
                name: "reservations",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    demand_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    demand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    demand_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reservations", x => x.id);
                    table.CheckConstraint("CK_reservations_quantity", "quantity > 0");
                    table.CheckConstraint("CK_reservations_status", "status IN ('Open','Consumed','Released','Expired')");
                });

            migrationBuilder.CreateTable(
                name: "serial_units",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    serial = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    current_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_serial_units", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stock_balances",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    on_hand = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    reserved = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    allows_negative = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_balances", x => x.id);
                    table.CheckConstraint("CK_stock_balances_on_hand_non_negative", "on_hand >= 0 OR allows_negative");
                    table.CheckConstraint("CK_stock_balances_reserved", "reserved >= 0 AND (allows_negative OR reserved <= on_hand)");
                });

            migrationBuilder.CreateTable(
                name: "adjustment_lines",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    quantity_delta = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    captured_uom = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    captured_qty = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    adjustment_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adjustment_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_adjustment_lines_adjustment",
                        column: x => x.adjustment_id,
                        principalSchema: "wms",
                        principalTable: "adjustments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adjustment_lines_position",
                schema: "wms",
                table: "adjustment_lines",
                columns: new[] { "adjustment_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_adjustments_tenant_number",
                schema: "wms",
                table: "adjustments",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_adjustments_tenant_status_warehouse",
                schema: "wms",
                table: "adjustments",
                columns: new[] { "tenant_id", "status", "warehouse_id" });

            migrationBuilder.CreateIndex(
                name: "IX_balance_checks_tenant_checked",
                schema: "wms",
                table: "balance_checks",
                columns: new[] { "tenant_id", "checked_at" });

            migrationBuilder.CreateIndex(
                name: "IX_import_previews_expires",
                schema: "wms",
                table: "import_previews",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_lots_owner_variant_code",
                schema: "wms",
                table: "lots",
                columns: new[] { "tenant_id", "owner_id", "variant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_operation_receipts_created",
                schema: "wms",
                table: "operation_receipts",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_reservations_demand",
                schema: "wms",
                table: "reservations",
                columns: new[] { "demand_type", "demand_id" });

            migrationBuilder.CreateIndex(
                name: "IX_reservations_open_key",
                schema: "wms",
                table: "reservations",
                columns: new[] { "tenant_id", "owner_id", "location_id", "variant_id", "lot_id", "status" },
                filter: "status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_serial_units_location",
                schema: "wms",
                table: "serial_units",
                column: "current_location_id");

            migrationBuilder.CreateIndex(
                name: "IX_serial_units_variant_serial",
                schema: "wms",
                table: "serial_units",
                columns: new[] { "tenant_id", "variant_id", "serial" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_balances_location",
                schema: "wms",
                table: "stock_balances",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_balances_tenant_variant_status",
                schema: "wms",
                table: "stock_balances",
                columns: new[] { "tenant_id", "variant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_balances_tenant_warehouse_variant",
                schema: "wms",
                table: "stock_balances",
                columns: new[] { "tenant_id", "warehouse_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "UQ_stock_balances_key",
                schema: "wms",
                table: "stock_balances",
                columns: new[] { "tenant_id", "owner_id", "location_id", "variant_id", "lot_id", "status" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.Sql(InventoryLedgerSql.CreateStockRowsView);
            migrationBuilder.Sql(InventoryLedgerSql.CreateStockSummaryView);
            migrationBuilder.Sql(InventoryLedgerSql.CreateInternalForeignKeys);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // El ledger es prueba de negocio: el Down no borra movimientos, saldos, vistas ni quita el trigger. Un rollback es
            // revertir el código y dejar el esquema. AddCustomerDataConsents sí borra en su Down y por eso no es el precedente.
            // Consecuencia aceptada: tras un Down el Up no se puede volver a aplicar sin intervención manual (el CreateTable de EF no
            // es idempotente).
        }
    }
}
