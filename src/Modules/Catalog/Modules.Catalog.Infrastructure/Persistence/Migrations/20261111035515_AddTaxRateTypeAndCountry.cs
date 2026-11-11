using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaxRateTypeAndCountry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "percentage",
                schema: "catalog",
                table: "tax_rates",
                type: "numeric(6,3)",
                precision: 6,
                scale: 3,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "country",
                schema: "catalog",
                table: "tax_rates",
                type: "character(2)",
                fixedLength: true,
                maxLength: 2,
                nullable: false,
                defaultValue: "CO");

            migrationBuilder.AddColumn<decimal>(
                name: "minimum_base",
                schema: "catalog",
                table: "tax_rates",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "type",
                schema: "catalog",
                table: "tax_rates",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "IVA");

            // Las filas que ya existen quedan como IVA de Colombia (spec §11.1); el default sólo sirve
            // para rellenarlas. Después se quita: el dominio exige tipo y país en cada alta.
            migrationBuilder.AlterColumn<string>(
                name: "country",
                schema: "catalog",
                table: "tax_rates",
                type: "character(2)",
                fixedLength: true,
                maxLength: 2,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character(2)",
                oldFixedLength: true,
                oldMaxLength: 2,
                oldDefaultValue: "CO");

            migrationBuilder.AlterColumn<string>(
                name: "type",
                schema: "catalog",
                table: "tax_rates",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldDefaultValue: "IVA");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "country",
                schema: "catalog",
                table: "tax_rates");

            migrationBuilder.DropColumn(
                name: "minimum_base",
                schema: "catalog",
                table: "tax_rates");

            migrationBuilder.DropColumn(
                name: "type",
                schema: "catalog",
                table: "tax_rates");

            migrationBuilder.AlterColumn<int>(
                name: "percentage",
                schema: "catalog",
                table: "tax_rates",
                type: "integer",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(6,3)",
                oldPrecision: 6,
                oldScale: 3);
        }
    }
}
