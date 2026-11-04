using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductVisibleInStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "visible_in_store",
                schema: "catalog",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_products_tenant_store_visible",
                schema: "catalog",
                table: "products",
                columns: new[] { "tenant_id", "name" },
                filter: "visible_in_store AND is_active");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_products_tenant_store_visible",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "visible_in_store",
                schema: "catalog",
                table: "products");
        }
    }
}
