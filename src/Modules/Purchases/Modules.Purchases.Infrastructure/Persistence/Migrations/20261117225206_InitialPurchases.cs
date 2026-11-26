using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Purchases.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPurchases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "purchases");

            migrationBuilder.CreateTable(
                name: "order_number_counters",
                schema: "purchases",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_number_counters", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "purchase_orders",
                schema: "purchases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    confirmed_with_changes = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_orders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "purchase_order_history",
                schema: "purchases",
                columns: table => new
                {
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_order_history", x => new { x.order_id, x.sequence });
                    table.ForeignKey(
                        name: "FK_purchase_order_history_purchase_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "purchases",
                        principalTable: "purchase_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "purchase_order_lines",
                schema: "purchases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    confirmed_quantity = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    confirmed_unit_cost = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    tax_rate_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_order_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_purchase_order_lines_purchase_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "purchases",
                        principalTable: "purchase_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_order_lines_order_ordinal",
                schema: "purchases",
                table: "purchase_order_lines",
                columns: new[] { "order_id", "ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_tenant_created",
                schema: "purchases",
                table: "purchase_orders",
                columns: new[] { "tenant_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_tenant_number",
                schema: "purchases",
                table: "purchase_orders",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_tenant_status",
                schema: "purchases",
                table: "purchase_orders",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_tenant_supplier",
                schema: "purchases",
                table: "purchase_orders",
                columns: new[] { "tenant_id", "supplier_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_number_counters",
                schema: "purchases");

            migrationBuilder.DropTable(
                name: "purchase_order_history",
                schema: "purchases");

            migrationBuilder.DropTable(
                name: "purchase_order_lines",
                schema: "purchases");

            migrationBuilder.DropTable(
                name: "purchase_orders",
                schema: "purchases");
        }
    }
}
