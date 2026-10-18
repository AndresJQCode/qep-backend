using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Shopify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreShopDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "shop_domain",
                schema: "shopify",
                table: "stores",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_shopify_stores_shop_domain",
                schema: "shopify",
                table: "stores",
                column: "shop_domain");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_shopify_stores_shop_domain",
                schema: "shopify",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "shop_domain",
                schema: "shopify",
                table: "stores");
        }
    }
}
