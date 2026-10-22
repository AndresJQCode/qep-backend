using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPosSaleCustomerIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_sales_tenant_customer",
                schema: "pos",
                table: "sales",
                columns: new[] { "tenant_id", "customer_id" },
                filter: "customer_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sales_tenant_customer",
                schema: "pos",
                table: "sales");
        }
    }
}
