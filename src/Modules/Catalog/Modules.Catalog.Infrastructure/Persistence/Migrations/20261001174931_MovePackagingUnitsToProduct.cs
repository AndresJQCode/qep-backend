using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// La unidad de empaque pasa de la escala al producto, y de un número a un conjunto
    /// (<c>Product.PackagingUnits</c>).
    ///
    /// Generada con el factory de diseño y editada a mano: EF sólo sabe agregar una columna y
    /// borrar la otra, y eso perdería el empaque de todas las escalas cargadas. Entre las dos, el
    /// producto hereda los valores distintos de sus escalas.
    ///
    /// **Si un producto tenía escalas con empaques distintos, la migración falla** y no los junta:
    /// hasta hoy cada escala exigía su propio empaque, y fundirlos en un conjunto cambiaría qué
    /// cantidades descuentan en cada tramo sin que nadie lo decida. El <c>RAISE</c> aborta la
    /// transacción entera y lista los productos a revisar.
    /// </summary>
    public partial class MovePackagingUnitsToProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int[]>(
                name: "packaging_units",
                schema: "catalog",
                table: "products",
                type: "integer[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.Sql(
                """
                UPDATE catalog.products AS product
                SET packaging_units = scales.units
                FROM (
                    SELECT product_id,
                           array_agg(DISTINCT packaging_unit ORDER BY packaging_unit) AS units
                    FROM catalog.product_price_scales
                    WHERE packaging_unit IS NOT NULL
                    GROUP BY product_id
                ) AS scales
                WHERE product.id = scales.product_id;
                """);

            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    conflicting text;
                BEGIN
                    SELECT string_agg(code || ' (' || id || ')', ', ' ORDER BY code)
                    INTO conflicting
                    FROM catalog.products
                    WHERE cardinality(packaging_units) > 1;

                    IF conflicting IS NOT NULL THEN
                        RAISE EXCEPTION 'Products whose price scales use different packaging units: %. Align their scales to a single packaging unit before migrating; they are not merged automatically.', conflicting;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "packaging_unit",
                schema: "catalog",
                table: "product_price_scales");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // La vuelta sólo existe con un empaque por producto: el modelo anterior guardaba un
            // número por escala, y no hay cómo repartir dos empaques entre las escalas sin inventar.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    conflicting text;
                BEGIN
                    SELECT string_agg(code || ' (' || id || ')', ', ' ORDER BY code)
                    INTO conflicting
                    FROM catalog.products
                    WHERE cardinality(packaging_units) > 1;

                    IF conflicting IS NOT NULL THEN
                        RAISE EXCEPTION 'Products with more than one packaging unit: %. Leave them with a single packaging unit before reverting this migration.', conflicting;
                    END IF;
                END $$;
                """);

            migrationBuilder.AddColumn<int>(
                name: "packaging_unit",
                schema: "catalog",
                table: "product_price_scales",
                type: "integer",
                nullable: true);

            // La restricción se guarda con el nombre del enum (HasConversion<string>).
            migrationBuilder.Sql(
                """
                UPDATE catalog.product_price_scales AS scale
                SET packaging_unit = product.packaging_units[1]
                FROM catalog.products AS product
                WHERE scale.product_id = product.id
                  AND scale.restriction = 'PackagingUnit'
                  AND cardinality(product.packaging_units) = 1;
                """);

            migrationBuilder.DropColumn(
                name: "packaging_units",
                schema: "catalog",
                table: "products");
        }
    }
}
