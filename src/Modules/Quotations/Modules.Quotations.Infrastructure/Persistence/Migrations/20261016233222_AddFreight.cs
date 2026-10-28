using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFreight : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "freight_tax_rate_id",
                schema: "quotations",
                table: "tenant_quotation_settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "carrier_id",
                schema: "quotations",
                table: "quotations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "carrier_name",
                schema: "quotations",
                table: "quotations",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "cash_on_delivery",
                schema: "quotations",
                table: "quotations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "freight_amount",
                schema: "quotations",
                table: "quotations",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "freight_charge_mode",
                schema: "quotations",
                table: "quotations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "freight_subtotal",
                schema: "quotations",
                table: "quotations",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "freight_tax_amount",
                schema: "quotations",
                table: "quotations",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "freight_tax_percentage",
                schema: "quotations",
                table: "quotations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "carrier_id",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "carrier_name",
                schema: "quotations",
                table: "orders",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "cash_on_delivery",
                schema: "quotations",
                table: "orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "freight_amount",
                schema: "quotations",
                table: "orders",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "freight_charge_mode",
                schema: "quotations",
                table: "orders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "freight_subtotal",
                schema: "quotations",
                table: "orders",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "freight_tax_amount",
                schema: "quotations",
                table: "orders",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "freight_tax_percentage",
                schema: "quotations",
                table: "orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "carriers",
                schema: "quotations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    supports_cash_on_delivery = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carriers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "carrier_names",
                schema: "quotations",
                columns: table => new
                {
                    normalized_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    carrier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    value = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carrier_names", x => new { x.carrier_id, x.normalized_name });
                    table.CheckConstraint("CK_carrier_names_kind", "kind IN ('Name','External')");
                    table.ForeignKey(
                        name: "FK_carrier_names_carriers_carrier_id",
                        column: x => x.carrier_id,
                        principalSchema: "quotations",
                        principalTable: "carriers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_quotations_freight_charge_mode",
                schema: "quotations",
                table: "quotations",
                sql: "freight_charge_mode IS NULL OR freight_charge_mode IN ('Charged','OnDelivery','Free')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_orders_freight_charge_mode",
                schema: "quotations",
                table: "orders",
                sql: "freight_charge_mode IS NULL OR freight_charge_mode IN ('Charged','OnDelivery','Free')");

            migrationBuilder.CreateIndex(
                name: "IX_carrier_names_tenant_name",
                schema: "quotations",
                table: "carrier_names",
                columns: new[] { "tenant_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_carriers_tenant_sort",
                schema: "quotations",
                table: "carriers",
                columns: new[] { "tenant_id", "sort_order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "carrier_names",
                schema: "quotations");

            migrationBuilder.DropTable(
                name: "carriers",
                schema: "quotations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_quotations_freight_charge_mode",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_orders_freight_charge_mode",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "freight_tax_rate_id",
                schema: "quotations",
                table: "tenant_quotation_settings");

            migrationBuilder.DropColumn(
                name: "carrier_id",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "carrier_name",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "cash_on_delivery",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "freight_amount",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "freight_charge_mode",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "freight_subtotal",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "freight_tax_amount",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "freight_tax_percentage",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "carrier_id",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "carrier_name",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "cash_on_delivery",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "freight_amount",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "freight_charge_mode",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "freight_subtotal",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "freight_tax_amount",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "freight_tax_percentage",
                schema: "quotations",
                table: "orders");
        }
    }
}
