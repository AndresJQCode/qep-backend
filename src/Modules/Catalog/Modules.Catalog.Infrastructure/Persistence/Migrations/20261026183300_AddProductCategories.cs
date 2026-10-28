using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "category_id",
                schema: "catalog",
                table: "products",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "product_categories",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_categories", x => x.id);
                    table.ForeignKey(
                        name: "FK_product_categories_product_categories_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "catalog",
                        principalTable: "product_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            // EF no modela índices sobre expresiones: el único se crea a mano (precedente
            // AddProductVariants). NULLS NOT DISTINCT para que dos raíces (parent_id NULL) con el
            // mismo nombre también choquen; CatalogUnitOfWork lo traduce por nombre.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_product_categories_tenant_parent_name\" ON catalog.product_categories (tenant_id, parent_id, lower(name)) NULLS NOT DISTINCT;");

            migrationBuilder.CreateIndex(
                name: "IX_products_category",
                schema: "catalog",
                table: "products",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_categories_parent",
                schema: "catalog",
                table: "product_categories",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_categories_tenant",
                schema: "catalog",
                table: "product_categories",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "FK_products_product_categories_category_id",
                schema: "catalog",
                table: "products",
                column: "category_id",
                principalSchema: "catalog",
                principalTable: "product_categories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_products_product_categories_category_id",
                schema: "catalog",
                table: "products");

            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS catalog.\"IX_product_categories_tenant_parent_name\";");

            migrationBuilder.DropTable(
                name: "product_categories",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "IX_products_category",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "category_id",
                schema: "catalog",
                table: "products");
        }
    }
}
