using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWarehouseTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tasks",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    priority = table.Column<short>(type: "smallint", nullable: false),
                    urgent = table.Column<bool>(type: "boolean", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    assigned_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_by_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_device_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tasks", x => x.id);
                    table.CheckConstraint("CK_tasks_assigned", "status <> 'Assigned' OR assigned_member_id IS NOT NULL");
                    table.CheckConstraint("CK_tasks_claim", "status <> 'InProgress' OR (claimed_by_member_id IS NOT NULL AND lease_token IS NOT NULL AND lease_expires_at IS NOT NULL)");
                    table.CheckConstraint("CK_tasks_priority", "priority BETWEEN 1 AND 9");
                    table.CheckConstraint("CK_tasks_released", "status NOT IN ('Ready','Assigned','InProgress') OR released_at IS NOT NULL");
                    table.CheckConstraint("CK_tasks_status", "status IN ('Planned','Ready','Assigned','InProgress','Done','Cancelled')");
                    table.CheckConstraint("CK_tasks_type", "type IN ('Move')");
                    table.ForeignKey(
                        name: "FK_tasks_owner",
                        column: x => x.owner_id,
                        principalSchema: "wms",
                        principalTable: "owners",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tasks_warehouse",
                        column: x => x.warehouse_id,
                        principalSchema: "wms",
                        principalTable: "warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "task_events",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_task_events_task",
                        column: x => x.task_id,
                        principalSchema: "wms",
                        principalTable: "tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "task_lines",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    stock_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    from_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity_planned = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    quantity_confirmed = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    line_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_lines", x => x.id);
                    table.CheckConstraint("CK_task_lines_quantity", "quantity_planned > 0 AND quantity_confirmed >= 0 AND quantity_confirmed <= quantity_planned");
                    table.CheckConstraint("CK_task_lines_status", "line_status IN ('Open','Done','Cancelled')");
                    table.ForeignKey(
                        name: "FK_task_lines_from_location",
                        column: x => x.from_location_id,
                        principalSchema: "wms",
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_task_lines_lot",
                        column: x => x.lot_id,
                        principalSchema: "wms",
                        principalTable: "lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_task_lines_task",
                        column: x => x.task_id,
                        principalSchema: "wms",
                        principalTable: "tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_task_lines_to_location",
                        column: x => x.to_location_id,
                        principalSchema: "wms",
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_task_events_task",
                schema: "wms",
                table: "task_events",
                columns: new[] { "task_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_task_lines_from_location_id",
                schema: "wms",
                table: "task_lines",
                column: "from_location_id");

            migrationBuilder.CreateIndex(
                name: "IX_task_lines_lot_id",
                schema: "wms",
                table: "task_lines",
                column: "lot_id");

            migrationBuilder.CreateIndex(
                name: "IX_task_lines_position",
                schema: "wms",
                table: "task_lines",
                columns: new[] { "task_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_task_lines_to_location_id",
                schema: "wms",
                table: "task_lines",
                column: "to_location_id");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_lease",
                schema: "wms",
                table: "tasks",
                column: "lease_expires_at",
                filter: "status = 'InProgress'");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_member_in_progress",
                schema: "wms",
                table: "tasks",
                columns: new[] { "tenant_id", "claimed_by_member_id" },
                unique: true,
                filter: "status = 'InProgress'");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_owner_id",
                schema: "wms",
                table: "tasks",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_pool",
                schema: "wms",
                table: "tasks",
                columns: new[] { "tenant_id", "warehouse_id", "status" },
                filter: "status IN ('Ready','Assigned')");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_source",
                schema: "wms",
                table: "tasks",
                columns: new[] { "tenant_id", "source_type", "source_id" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_tenant_number",
                schema: "wms",
                table: "tasks",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tasks_warehouse_id",
                schema: "wms",
                table: "tasks",
                column: "warehouse_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_events",
                schema: "wms");

            migrationBuilder.DropTable(
                name: "task_lines",
                schema: "wms");

            migrationBuilder.DropTable(
                name: "tasks",
                schema: "wms");
        }
    }
}
