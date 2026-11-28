using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBasicInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "general_location_id",
                schema: "wms",
                table: "warehouses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_principal",
                schema: "wms",
                table: "warehouses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<Guid>(
                name: "location_id",
                schema: "wms",
                table: "reservations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "document_stock_states",
                schema: "wms",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_version = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    posting_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    posting_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    skipped_line_ids = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_stock_states", x => new { x.tenant_id, x.document_type, x.document_id });
                    table.CheckConstraint("CK_document_stock_states_status", "status IN ('None','Reserved','TakenOutByPrepare','TakenOutByArchive','Released','Parked','HandedToOutbound','Received','ReceivedByWms','Shipped','Voided')");
                    table.CheckConstraint("CK_document_stock_states_type", "document_type IN ('PurchaseOrder','Order','PosSale')");
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "wms",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claimed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_messages", x => new { x.consumer, x.message_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_warehouses_tenant_principal",
                schema: "wms",
                table: "warehouses",
                column: "tenant_id",
                unique: true,
                filter: "is_principal");

            migrationBuilder.CreateIndex(
                name: "IX_reservations_open_demand",
                schema: "wms",
                table: "reservations",
                columns: new[] { "tenant_id", "demand_type", "demand_id", "demand_line_id" },
                unique: true,
                filter: "status = 'Open' AND location_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_document_stock_states_tenant_status",
                schema: "wms",
                table: "document_stock_states",
                columns: new[] { "tenant_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_stock_states",
                schema: "wms");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "wms");

            migrationBuilder.DropIndex(
                name: "IX_warehouses_tenant_principal",
                schema: "wms",
                table: "warehouses");

            migrationBuilder.DropIndex(
                name: "IX_reservations_open_demand",
                schema: "wms",
                table: "reservations");

            migrationBuilder.DropColumn(
                name: "general_location_id",
                schema: "wms",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "is_principal",
                schema: "wms",
                table: "warehouses");

            migrationBuilder.AlterColumn<Guid>(
                name: "location_id",
                schema: "wms",
                table: "reservations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
