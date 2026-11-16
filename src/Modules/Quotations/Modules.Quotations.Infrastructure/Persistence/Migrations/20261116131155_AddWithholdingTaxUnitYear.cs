using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec retenciones por UVT §5.2 y §8: el año de la UVT congelada en cada tasa. Las filas que ya tienen valor se
    /// rellenan con el mayor año cargado del mismo país y el mismo valor que no pase del año del documento (owner P2); lo
    /// que no case queda null y se muestra el valor sin año y sin aviso.
    /// </summary>
    public partial class AddWithholdingTaxUnitYear : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "tax_unit_year",
                schema: "quotations",
                table: "quotation_withholdings",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "tax_unit_year",
                schema: "quotations",
                table: "order_withholdings",
                type: "smallint",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE quotations.quotation_withholdings w
                SET tax_unit_year = (
                  SELECT max(t.year) FROM catalog.tax_unit_values t
                  WHERE t.country = w.country AND t.value = w.tax_unit_value
                    AND t.year <= extract(year FROM d.created_at))
                FROM quotations.quotations d
                WHERE d.id = w.document_id AND w.tax_unit_value IS NOT NULL;
                """);

            migrationBuilder.Sql(
                """
                UPDATE quotations.order_withholdings w
                SET tax_unit_year = (
                  SELECT max(t.year) FROM catalog.tax_unit_values t
                  WHERE t.country = w.country AND t.value = w.tax_unit_value
                    AND t.year <= extract(year FROM d.created_at))
                FROM quotations.orders d
                WHERE d.id = w.document_id AND w.tax_unit_value IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "tax_unit_year",
                schema: "quotations",
                table: "quotation_withholdings");

            migrationBuilder.DropColumn(
                name: "tax_unit_year",
                schema: "quotations",
                table: "order_withholdings");
        }
    }
}
