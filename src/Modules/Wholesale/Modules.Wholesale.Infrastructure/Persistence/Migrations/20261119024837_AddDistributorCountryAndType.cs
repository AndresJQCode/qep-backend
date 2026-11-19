using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wholesale.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDistributorCountryAndType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_distributors_tenant_identification",
                schema: "wholesale",
                table: "distributors");

            migrationBuilder.AddColumn<string>(
                name: "country",
                schema: "wholesale",
                table: "distributors",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                // Los distribuidores anteriores eran colombianos con NIT: así quedan, sin default en el modelo.
                defaultValue: "CO");

            migrationBuilder.AddColumn<string>(
                name: "identification_type",
                schema: "wholesale",
                table: "distributors",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "NIT");

            // El default sólo rellena las filas que ya existían: el modelo no lo tiene, y toda escritura manda los dos.
            migrationBuilder.Sql("ALTER TABLE wholesale.distributors ALTER COLUMN country DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE wholesale.distributors ALTER COLUMN identification_type DROP DEFAULT;");

            migrationBuilder.CreateIndex(
                name: "IX_distributors_tenant_identification",
                schema: "wholesale",
                table: "distributors",
                columns: new[] { "tenant_id", "identification_type", "identification_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_distributors_tenant_identification",
                schema: "wholesale",
                table: "distributors");

            migrationBuilder.DropColumn(
                name: "country",
                schema: "wholesale",
                table: "distributors");

            migrationBuilder.DropColumn(
                name: "identification_type",
                schema: "wholesale",
                table: "distributors");

            // Falla si ya hay el mismo número con dos tipos: el índice viejo no los distingue. Quien revierta lo resuelve antes.
            migrationBuilder.CreateIndex(
                name: "IX_distributors_tenant_identification",
                schema: "wholesale",
                table: "distributors",
                columns: new[] { "tenant_id", "identification_number" },
                unique: true);
        }
    }
}
