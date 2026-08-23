using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceCustomerPriceListWithAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "price_list_id",
                schema: "customers",
                table: "customers");

            migrationBuilder.CreateTable(
                name: "customer_price_lists",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_price_lists", x => x.id);
                    table.ForeignKey(
                        name: "FK_customer_price_lists_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "customers",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_price_lists_customer_price_list",
                schema: "customers",
                table: "customer_price_lists",
                columns: new[] { "customer_id", "price_list_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_price_lists_price_list",
                schema: "customers",
                table: "customer_price_lists",
                column: "price_list_id");

            migrationBuilder.CreateIndex(
                name: "IX_customer_price_lists_tenant",
                schema: "customers",
                table: "customer_price_lists",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_price_lists",
                schema: "customers");

            migrationBuilder.AddColumn<Guid>(
                name: "price_list_id",
                schema: "customers",
                table: "customers",
                type: "uuid",
                nullable: true);
        }
    }
}
