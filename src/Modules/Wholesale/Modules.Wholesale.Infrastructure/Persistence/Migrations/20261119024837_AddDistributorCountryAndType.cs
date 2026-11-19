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

            migrationBuilder.CreateIndex(
                name: "IX_distributors_tenant_identification",
                schema: "wholesale",
                table: "distributors",
                columns: new[] { "tenant_id", "identification_number" },
                unique: true);
        }
    }
}
