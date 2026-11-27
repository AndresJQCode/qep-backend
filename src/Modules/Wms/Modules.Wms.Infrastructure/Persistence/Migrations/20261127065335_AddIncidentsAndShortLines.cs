using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIncidentsAndShortLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_task_lines_status",
                schema: "wms",
                table: "task_lines");

            migrationBuilder.CreateTable(
                name: "incidents",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_task_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    verification_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    stock_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    expected_qty = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    actual_qty = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    raw_code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    raw_payload = table.Column<string>(type: "jsonb", nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reported_by = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    operation_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    resolution = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    adjustment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_incident_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidents", x => x.id);
                    table.CheckConstraint("CK_incidents_quantities", "(expected_qty IS NULL OR expected_qty >= 0) AND (actual_qty IS NULL OR actual_qty >= 0)");
                    table.CheckConstraint("CK_incidents_resolution", "resolution IS NULL OR resolution IN ('Accept','AcceptIntoStock')");
                    table.CheckConstraint("CK_incidents_resolved", "status <> 'Resolved' OR (resolution IS NOT NULL AND resolved_at IS NOT NULL)");
                    table.CheckConstraint("CK_incidents_status", "status IN ('Open','InReview','Resolved','Rejected')");
                    table.CheckConstraint("CK_incidents_type", "type IN ('ShortMove','LocationMismatch','UnreadableLabel','CountVariance','SyncConflict')");
                    table.ForeignKey(
                        name: "FK_incidents_location",
                        column: x => x.location_id,
                        principalSchema: "wms",
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_incidents_lot",
                        column: x => x.lot_id,
                        principalSchema: "wms",
                        principalTable: "lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_incidents_owner",
                        column: x => x.owner_id,
                        principalSchema: "wms",
                        principalTable: "owners",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_incidents_parent",
                        column: x => x.parent_incident_id,
                        principalSchema: "wms",
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_incidents_warehouse",
                        column: x => x.warehouse_id,
                        principalSchema: "wms",
                        principalTable: "warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "incident_events",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incident_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_incident_events_incident",
                        column: x => x.incident_id,
                        principalSchema: "wms",
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "incident_evidence",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    raw_value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incident_evidence", x => x.id);
                    table.CheckConstraint("CK_incident_evidence_kind", "kind IN ('Photo','Scan')");
                    table.ForeignKey(
                        name: "FK_incident_evidence_incident",
                        column: x => x.incident_id,
                        principalSchema: "wms",
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_task_lines_status",
                schema: "wms",
                table: "task_lines",
                sql: "line_status IN ('Open','Done','Short','Cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_incident_events_incident",
                schema: "wms",
                table: "incident_events",
                columns: new[] { "incident_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_incident_evidence_position",
                schema: "wms",
                table: "incident_evidence",
                columns: new[] { "incident_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_incidents_adjustment",
                schema: "wms",
                table: "incidents",
                column: "adjustment_id",
                filter: "adjustment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_location_id",
                schema: "wms",
                table: "incidents",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_lot_id",
                schema: "wms",
                table: "incidents",
                column: "lot_id");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_open",
                schema: "wms",
                table: "incidents",
                columns: new[] { "tenant_id", "warehouse_id", "status" },
                filter: "status IN ('Open','InReview')");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_operation_key",
                schema: "wms",
                table: "incidents",
                columns: new[] { "tenant_id", "operation_key" },
                unique: true,
                filter: "operation_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_owner_id",
                schema: "wms",
                table: "incidents",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_parent_incident_id",
                schema: "wms",
                table: "incidents",
                column: "parent_incident_id");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_tenant_number",
                schema: "wms",
                table: "incidents",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_incidents_variant_open",
                schema: "wms",
                table: "incidents",
                columns: new[] { "tenant_id", "variant_id" },
                filter: "status IN ('Open','InReview') AND variant_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_verification",
                schema: "wms",
                table: "incidents",
                column: "verification_task_id",
                filter: "verification_task_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_warehouse_id",
                schema: "wms",
                table: "incidents",
                column: "warehouse_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Bajar con líneas Short falla con 23514 al volver al CHECK de T1: a propósito. No hay a qué estado devolver una línea que
            // ya quedó Short, y las novedades ya guardadas se pierden con sus tablas; el camino es corregir hacia adelante.
            migrationBuilder.DropTable(
                name: "incident_events",
                schema: "wms");

            migrationBuilder.DropTable(
                name: "incident_evidence",
                schema: "wms");

            migrationBuilder.DropTable(
                name: "incidents",
                schema: "wms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_task_lines_status",
                schema: "wms",
                table: "task_lines");

            migrationBuilder.AddCheckConstraint(
                name: "CK_task_lines_status",
                schema: "wms",
                table: "task_lines",
                sql: "line_status IN ('Open','Done','Cancelled')");
        }
    }
}
