using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantCustomerFieldPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "customer_city_required_on_billing",
                schema: "tenancy",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "customer_city_required_on_save",
                schema: "tenancy",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "customer_document_required_on_billing",
                schema: "tenancy",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "customer_document_required_on_save",
                schema: "tenancy",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "customer_city_required_on_billing",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "customer_city_required_on_save",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "customer_document_required_on_billing",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "customer_document_required_on_save",
                schema: "tenancy",
                table: "tenants");
        }
    }
}
