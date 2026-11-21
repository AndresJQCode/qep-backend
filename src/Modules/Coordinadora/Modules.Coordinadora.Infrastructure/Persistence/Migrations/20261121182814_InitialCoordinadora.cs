using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Coordinadora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCoordinadora : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "coordinadora");

            migrationBuilder.CreateTable(
                name: "settings",
                schema: "coordinadora",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    sender_address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    sender_phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    sender_city_id = table.Column<Guid>(type: "uuid", nullable: true),
                    account_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    product_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    service_level = table.Column<int>(type: "integer", nullable: true),
                    div = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    default_height_cm = table.Column<int>(type: "integer", nullable: true),
                    default_width_cm = table.Column<int>(type: "integer", nullable: true),
                    default_length_cm = table.Column<int>(type: "integer", nullable: true),
                    default_weight_kg = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    packaging_type = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    declared_value_minimum = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false, defaultValue: 0m),
                    collect_on_delivery = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    label_format = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.connection_id);
                    table.CheckConstraint("CK_settings_declared_value_minimum", "declared_value_minimum >= 0");
                    table.CheckConstraint("CK_settings_packaging_type", "packaging_type IN (0, 1)");
                });

            migrationBuilder.CreateTable(
                name: "shipments",
                schema: "coordinadora",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    request_json = table.Column<string>(type: "text", nullable: false),
                    guide_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    declared_value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    label_pdf = table.Column<byte[]>(type: "bytea", nullable: true),
                    tracking_status = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    tracking_detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    tracked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    error_detail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    order_version = table.Column<long>(type: "bigint", nullable: false),
                    sending_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipments", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_settings_tenant",
                schema: "coordinadora",
                table: "settings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_shipments_tenant_order",
                schema: "coordinadora",
                table: "shipments",
                columns: new[] { "tenant_id", "order_id" });

            migrationBuilder.CreateIndex(
                name: "IX_shipments_tenant_order_live",
                schema: "coordinadora",
                table: "shipments",
                columns: new[] { "tenant_id", "order_id" },
                unique: true,
                filter: "status IN ('Pending', 'Created', 'Unknown')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "settings",
                schema: "coordinadora");

            migrationBuilder.DropTable(
                name: "shipments",
                schema: "coordinadora");
        }
    }
}
