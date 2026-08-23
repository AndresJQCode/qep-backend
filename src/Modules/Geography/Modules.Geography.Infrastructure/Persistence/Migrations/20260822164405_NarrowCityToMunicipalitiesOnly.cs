using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Geography.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NarrowCityToMunicipalitiesOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Los centros poblados/corregimientos (código de 8 dígitos) dejaron de importarse:
            // su nombre se repite masivamente dentro de un mismo departamento (ver
            // DivipolaDataParser). Ninguna fila de customers.customers referencia una ciudad de 8
            // dígitos (verificado antes de esta migración), así que el borrado es seguro.
            migrationBuilder.Sql(
                "DELETE FROM geography.cities WHERE length(divipola_code) = 8;");

            migrationBuilder.AlterColumn<string>(
                name: "divipola_code",
                schema: "geography",
                table: "cities",
                type: "character varying(5)",
                maxLength: 5,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(8)",
                oldMaxLength: 8);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "divipola_code",
                schema: "geography",
                table: "cities",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(5)",
                oldMaxLength: 5);
        }
    }
}
