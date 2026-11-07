using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Companies.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Las empresas que ya existen eran todas colombianas con NIT y ciudad del catalogo: los
    // defaults 'CO'/'NIT' de abajo son el backfill, y se quitan enseguida para que una fila nueva
    // tenga que decir su pais y su tipo de documento. Con ese estado previo las dos constraints
    // pasan sin tocar ninguna fila.
    public partial class AddCompanyCountryAndDocumentType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "country",
                schema: "companies",
                table: "companies",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "CO");

            migrationBuilder.AddColumn<string>(
                name: "document_type",
                schema: "companies",
                table: "companies",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "NIT");

            migrationBuilder.AlterColumn<string>(
                name: "country",
                schema: "companies",
                table: "companies",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2)",
                oldMaxLength: 2,
                oldDefaultValue: "CO");

            migrationBuilder.AlterColumn<string>(
                name: "document_type",
                schema: "companies",
                table: "companies",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldDefaultValue: "NIT");

            migrationBuilder.AddColumn<string>(
                name: "city_name",
                schema: "companies",
                table: "companies",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            // La FK FK_companies_cities_city_id (AddCompanyCity) no se toca: una FK nullable es valida.
            migrationBuilder.AlterColumn<Guid>(
                name: "city_id",
                schema: "companies",
                table: "companies",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddCheckConstraint(
                name: "CK_companies_city_lane",
                schema: "companies",
                table: "companies",
                sql: "(country = 'CO' AND city_id IS NOT NULL AND city_name IS NULL) OR (country <> 'CO' AND city_id IS NULL AND city_name IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_companies_document_type",
                schema: "companies",
                table: "companies",
                sql: "document_type IN ('NIT','CC','CE','PASAPORTE','NIF','NIE','TAX_ID')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_companies_city_lane",
                schema: "companies",
                table: "companies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_companies_document_type",
                schema: "companies",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "city_name",
                schema: "companies",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "country",
                schema: "companies",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "document_type",
                schema: "companies",
                table: "companies");

            // Falla a proposito si existe una empresa de fuera de Colombia: su city_id es NULL y no
            // hay una ciudad del catalogo que inventarle. Mejor un error claro que datos falsos.
            migrationBuilder.AlterColumn<Guid>(
                name: "city_id",
                schema: "companies",
                table: "companies",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
