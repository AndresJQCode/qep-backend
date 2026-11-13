using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderWithholdings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "customer_with_retention",
                schema: "quotations",
                table: "orders",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.CreateTable(
                name: "order_withholdings",
                schema: "quotations",
                columns: table => new
                {
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    position = table.Column<short>(type: "smallint", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tax_rate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    country = table.Column<string>(type: "char(2)", maxLength: 2, nullable: false),
                    percentage = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    minimum_base = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    minimum_base_unit = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    tax_unit_value = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    base_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    applied = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_withholdings", x => new { x.document_id, x.source, x.position });
                    table.CheckConstraint("CK_order_withholdings_source", "source IN ('Document')");
                    table.ForeignKey(
                        name: "FK_order_withholdings_orders_document_id",
                        column: x => x.document_id,
                        principalSchema: "quotations",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_withholdings",
                schema: "quotations");

            migrationBuilder.AlterColumn<bool>(
                name: "customer_with_retention",
                schema: "quotations",
                table: "orders",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);
        }
    }
}
