using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantCountry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "country",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                // Los tenants existentes quedan en Colombia, el mismo valor con que Tenant.Create
                // arranca: con "" el GET devolvería un país que el PUT rechaza.
                defaultValue: "CO");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "country",
                schema: "tenancy",
                table: "tenants");
        }
    }
}
