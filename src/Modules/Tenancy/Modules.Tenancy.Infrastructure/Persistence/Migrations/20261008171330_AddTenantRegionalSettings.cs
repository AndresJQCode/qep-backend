using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantRegionalSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "default_currency",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "COP");

            migrationBuilder.AddColumn<string>(
                name: "number_format",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "1.234,56");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_currency",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "number_format",
                schema: "tenancy",
                table: "tenants");
        }
    }
}
