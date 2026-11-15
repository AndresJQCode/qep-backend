using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec ReteICA por empresa §7.5 y §11.1: el municipio de cada tasa congelada (null en las nacionales) y el porcentaje
    /// del catálogo junto al efectivo. Las filas que ya existen se rellenan con su propio <c>percentage</c>, así ninguna
    /// queda como editada y ningún total se mueve (Review Focus 5). La columna nace nullable, se rellena y después pasa a
    /// NOT NULL: un default 0 dejaría todas las filas viejas como «editadas».
    /// </summary>
    public partial class AddWithholdingCityAndCatalogPercentage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "quotation_withholdings", "order_withholdings" })
            {
                migrationBuilder.AddColumn<decimal>(
                    name: "catalog_percentage",
                    schema: "quotations",
                    table: table,
                    type: "numeric(6,3)",
                    precision: 6,
                    scale: 3,
                    nullable: true);

                migrationBuilder.AddColumn<Guid>(
                    name: "city_id",
                    schema: "quotations",
                    table: table,
                    type: "uuid",
                    nullable: true);

                migrationBuilder.Sql(
                    $"UPDATE quotations.{table} SET catalog_percentage = percentage;");
                migrationBuilder.Sql(
                    $"ALTER TABLE quotations.{table} ALTER COLUMN catalog_percentage SET NOT NULL;");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "catalog_percentage",
                schema: "quotations",
                table: "quotation_withholdings");

            migrationBuilder.DropColumn(
                name: "city_id",
                schema: "quotations",
                table: "quotation_withholdings");

            migrationBuilder.DropColumn(
                name: "catalog_percentage",
                schema: "quotations",
                table: "order_withholdings");

            migrationBuilder.DropColumn(
                name: "city_id",
                schema: "quotations",
                table: "order_withholdings");
        }
    }
}
