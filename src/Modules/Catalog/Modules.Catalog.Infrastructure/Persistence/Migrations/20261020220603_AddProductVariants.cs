using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec 2026-10-20 (variantes de producto) §8. Cada producto pasa a ser un producto sin
    /// opciones con una variante por defecto que lleva su mismo id, su código, sus precios, sus
    /// escalas y sus empaques; la imagen pasa a ser la primera foto de la galería y el historial de
    /// precios queda en esa variante. Crea además la biblioteca de opciones (option_definitions,
    /// option_values) y las tablas que la enlazan con productos y variantes.
    ///
    /// Generada con el factory de diseño y editada a mano: los datos se copian **antes** de borrar
    /// las columnas de products, la columna variant_id del historial se llena antes de pasar a NOT
    /// NULL (el scaffold le dejaba un DEFAULT de Guid vacío), y el único sin mayúsculas de las
    /// opciones va en SQL. Las columnas heredadas sin mapear (price_base_*, final_*) se quedan: el
    /// Down de AddProductPrices las necesita. El Designer y el snapshot quedan como se generaron.
    /// </summary>
    public partial class AddProductVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Las FK de precios y escalas apuntan a products; al final apuntan a product_variants.
            migrationBuilder.DropForeignKey(
                name: "FK_product_price_scales_products_product_id",
                schema: "catalog",
                table: "product_price_scales");

            migrationBuilder.DropForeignKey(
                name: "FK_product_prices_products_product_id",
                schema: "catalog",
                table: "product_prices");

            migrationBuilder.CreateTable(
                name: "option_definitions",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_option_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product_gallery_images",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_gallery_images", x => x.id);
                    table.ForeignKey(
                        name: "FK_product_gallery_images_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "option_values",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    option_definition_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_option_values", x => x.id);
                    table.ForeignKey(
                        name: "FK_option_values_option_definitions_option_definition_id",
                        column: x => x.option_definition_id,
                        principalSchema: "catalog",
                        principalTable: "option_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_options",
                schema: "catalog",
                columns: table => new
                {
                    position = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    option_definition_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_options", x => new { x.product_id, x.position });
                    table.ForeignKey(
                        name: "FK_product_options_option_definitions_option_definition_id",
                        column: x => x.option_definition_id,
                        principalSchema: "catalog",
                        principalTable: "option_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_product_options_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_variants",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    barcode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    combination_key = table.Column<string>(type: "text", nullable: false),
                    packaging_units = table.Column<int[]>(type: "integer[]", nullable: false, defaultValueSql: "'{}'"),
                    gallery_image_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_variants", x => x.id);
                    table.ForeignKey(
                        name: "FK_product_variants_product_gallery_images_gallery_image_id",
                        column: x => x.gallery_image_id,
                        principalSchema: "catalog",
                        principalTable: "product_gallery_images",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_product_variants_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_variant_option_values",
                schema: "catalog",
                columns: table => new
                {
                    position = table.Column<int>(type: "integer", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    option_value_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_variant_option_values", x => new { x.variant_id, x.position });
                    table.ForeignKey(
                        name: "FK_product_variant_option_values_option_values_option_value_id",
                        column: x => x.option_value_id,
                        principalSchema: "catalog",
                        principalTable: "option_values",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_product_variant_option_values_product_variants_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Datos (spec §8), **antes** de borrar code, image_file_id y packaging_units de products.
            // La imagen vieja pasa a ser la primera foto de la galería.
            migrationBuilder.Sql("""
                INSERT INTO catalog.product_gallery_images (id, product_id, file_id, position)
                SELECT gen_random_uuid(), id, image_file_id, 0
                FROM catalog.products
                WHERE image_file_id IS NOT NULL;
                """);

            // Una variante por defecto por producto, con el mismo id: así quotation_items, order_items
            // y sale_lines rellenan variant_id = product_id sin consultar entre esquemas, y
            // product_prices/product_price_scales conservan sus filas sin copiarlas (el product_id que
            // ya tienen es el id de la variante). Activa siempre: la venta la apaga el producto
            // inactivo, y reactivarlo no tiene por qué tocar la variante.
            migrationBuilder.Sql("""
                INSERT INTO catalog.product_variants (
                    id, product_id, tenant_id, code, barcode, combination_key, packaging_units,
                    gallery_image_id, is_active, position)
                SELECT p.id, p.id, p.tenant_id, p.code, NULL, '', p.packaging_units, g.id, true, 0
                FROM catalog.products p
                LEFT JOIN catalog.product_gallery_images g ON g.product_id = p.id AND g.position = 0;
                """);

            migrationBuilder.DropIndex(
                name: "IX_products_code_trgm",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_tenant_code",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "code",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "image_file_id",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "packaging_units",
                schema: "catalog",
                table: "products");

            migrationBuilder.RenameColumn(
                name: "product_id",
                schema: "catalog",
                table: "product_prices",
                newName: "variant_id");

            migrationBuilder.RenameColumn(
                name: "product_id",
                schema: "catalog",
                table: "product_price_scales",
                newName: "variant_id");

            migrationBuilder.RenameIndex(
                name: "IX_product_price_scales_product",
                schema: "catalog",
                table: "product_price_scales",
                newName: "IX_product_price_scales_variant");

            // El historial queda en la variante por defecto (spec §8). Nullable para llenarla y NOT
            // NULL después: el scaffold le ponía un DEFAULT de Guid vacío que se quedaba para siempre.
            migrationBuilder.AddColumn<Guid>(
                name: "variant_id",
                schema: "catalog",
                table: "product_price_changes",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("UPDATE catalog.product_price_changes SET variant_id = product_id;");

            migrationBuilder.AlterColumn<Guid>(
                name: "variant_id",
                schema: "catalog",
                table: "product_price_changes",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            // Nombre único por tenant sin distinguir mayúsculas (spec §3.1). A mano porque EF no
            // modela índices sobre expresiones; mismo patrón que IX_tags_tenant_name.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_option_definitions_tenant_name\" ON catalog.option_definitions (tenant_id, lower(name));");

            migrationBuilder.CreateIndex(
                name: "IX_product_price_changes_variant",
                schema: "catalog",
                table: "product_price_changes",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "IX_option_definitions_tenant",
                schema: "catalog",
                table: "option_definitions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_option_values_option_definition",
                schema: "catalog",
                table: "option_values",
                column: "option_definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_gallery_images_product",
                schema: "catalog",
                table: "product_gallery_images",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_options_option_definition",
                schema: "catalog",
                table: "product_options",
                column: "option_definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_variant_option_values_option_value",
                schema: "catalog",
                table: "product_variant_option_values",
                column: "option_value_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_variants_code_trgm",
                schema: "catalog",
                table: "product_variants",
                column: "code")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_product_variants_gallery_image_id",
                schema: "catalog",
                table: "product_variants",
                column: "gallery_image_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_variants_product_combination",
                schema: "catalog",
                table: "product_variants",
                columns: new[] { "product_id", "combination_key" });

            migrationBuilder.CreateIndex(
                name: "IX_product_variants_tenant_barcode",
                schema: "catalog",
                table: "product_variants",
                columns: new[] { "tenant_id", "barcode" },
                unique: true,
                filter: "barcode IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_product_variants_tenant_code",
                schema: "catalog",
                table: "product_variants",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_product_price_changes_product_variants_variant_id",
                schema: "catalog",
                table: "product_price_changes",
                column: "variant_id",
                principalSchema: "catalog",
                principalTable: "product_variants",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_product_price_scales_product_variants_variant_id",
                schema: "catalog",
                table: "product_price_scales",
                column: "variant_id",
                principalSchema: "catalog",
                principalTable: "product_variants",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_product_prices_product_variants_variant_id",
                schema: "catalog",
                table: "product_prices",
                column: "variant_id",
                principalSchema: "catalog",
                principalTable: "product_variants",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_product_price_changes_product_variants_variant_id",
                schema: "catalog",
                table: "product_price_changes");

            migrationBuilder.DropForeignKey(
                name: "FK_product_price_scales_product_variants_variant_id",
                schema: "catalog",
                table: "product_price_scales");

            migrationBuilder.DropForeignKey(
                name: "FK_product_prices_product_variants_variant_id",
                schema: "catalog",
                table: "product_prices");

            migrationBuilder.AddColumn<string>(
                name: "code",
                schema: "catalog",
                table: "products",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "image_file_id",
                schema: "catalog",
                table: "products",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int[]>(
                name: "packaging_units",
                schema: "catalog",
                table: "products",
                type: "integer[]",
                nullable: false,
                defaultValueSql: "'{}'");

            // El modelo de antes tiene una sola variante por producto: vuelve la que lleva el id del
            // producto (la por defecto, o la que la conservó al agregar opciones) y, si ya no existe,
            // la primera. Las demás variantes, con sus precios y escalas, no tienen dónde vivir y se
            // pierden; su historial se queda, colgado del producto.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE variant_rollback_choice ON COMMIT DROP AS
                SELECT DISTINCT ON (product_id) id, product_id, code, packaging_units
                FROM catalog.product_variants
                ORDER BY product_id, (id = product_id) DESC, position;

                UPDATE catalog.products p
                SET code = c.code, packaging_units = c.packaging_units
                FROM variant_rollback_choice c
                WHERE c.product_id = p.id;

                UPDATE catalog.products p
                SET image_file_id = g.file_id
                FROM catalog.product_gallery_images g
                WHERE g.product_id = p.id AND g.position = 0;

                DELETE FROM catalog.product_prices
                WHERE variant_id NOT IN (SELECT id FROM variant_rollback_choice);
                UPDATE catalog.product_prices pr
                SET variant_id = c.product_id
                FROM variant_rollback_choice c
                WHERE pr.variant_id = c.id AND c.id <> c.product_id;

                DELETE FROM catalog.product_price_scales
                WHERE variant_id NOT IN (SELECT id FROM variant_rollback_choice);
                UPDATE catalog.product_price_scales s
                SET variant_id = c.product_id
                FROM variant_rollback_choice c
                WHERE s.variant_id = c.id AND c.id <> c.product_id;

                DROP TABLE variant_rollback_choice;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "code",
                schema: "catalog",
                table: "products",
                type: "character varying(60)",
                maxLength: 60,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(60)",
                oldMaxLength: 60,
                oldNullable: true);

            migrationBuilder.DropTable(
                name: "product_options",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_variant_option_values",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "option_values",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_variants",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "option_definitions",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_gallery_images",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "IX_product_price_changes_variant",
                schema: "catalog",
                table: "product_price_changes");

            migrationBuilder.DropColumn(
                name: "variant_id",
                schema: "catalog",
                table: "product_price_changes");

            migrationBuilder.RenameColumn(
                name: "variant_id",
                schema: "catalog",
                table: "product_prices",
                newName: "product_id");

            migrationBuilder.RenameColumn(
                name: "variant_id",
                schema: "catalog",
                table: "product_price_scales",
                newName: "product_id");

            migrationBuilder.RenameIndex(
                name: "IX_product_price_scales_variant",
                schema: "catalog",
                table: "product_price_scales",
                newName: "IX_product_price_scales_product");

            migrationBuilder.CreateIndex(
                name: "IX_products_code_trgm",
                schema: "catalog",
                table: "products",
                column: "code")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_products_tenant_code",
                schema: "catalog",
                table: "products",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_product_price_scales_products_product_id",
                schema: "catalog",
                table: "product_price_scales",
                column: "product_id",
                principalSchema: "catalog",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_product_prices_products_product_id",
                schema: "catalog",
                table: "product_prices",
                column: "product_id",
                principalSchema: "catalog",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
