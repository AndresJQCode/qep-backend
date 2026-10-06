using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Geography.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Separa el nombre oficial del DANE (<c>divipola_name</c>) del nombre que se muestra
    /// (<c>name</c>), que desde ahora puede ser el nombre común del municipio ("CALI").
    ///
    /// Editada a mano: EF la generó como NOT NULL con <c>DEFAULT ''</c>, que dejaba las filas
    /// existentes con el nombre DANE vacío hasta el siguiente arranque y un default que nada usa.
    /// Acá la columna nace nullable, se llena con <c>name</c> —que hasta esta migración es siempre
    /// el nombre DANE— y después pasa a NOT NULL sin default. El nombre común lo pone
    /// <c>GeographySeeder</c> en el arranque, no la migración.
    /// </summary>
    public partial class AddCityDivipolaName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "divipola_name",
                schema: "geography",
                table: "cities",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.Sql("UPDATE geography.cities SET divipola_name = name;");

            migrationBuilder.AlterColumn<string>(
                name: "divipola_name",
                schema: "geography",
                table: "cities",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(120)",
                oldMaxLength: 120,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Antes de esta migración name era siempre el nombre DANE: se le devuelve, para que
            // una ciudad con nombre común no quede mostrándolo sin la columna que lo explica.
            migrationBuilder.Sql("UPDATE geography.cities SET name = divipola_name;");

            migrationBuilder.DropColumn(
                name: "divipola_name",
                schema: "geography",
                table: "cities");
        }
    }
}
