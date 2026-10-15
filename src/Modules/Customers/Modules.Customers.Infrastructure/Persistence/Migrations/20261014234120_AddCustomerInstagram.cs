using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerInstagram : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "instagram_user_id",
                schema: "customers",
                table: "customers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "instagram_username",
                schema: "customers",
                table: "customers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_customers_tenant_instagram_user_id",
                schema: "customers",
                table: "customers",
                columns: new[] { "tenant_id", "instagram_user_id" },
                unique: true,
                filter: "instagram_user_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_customers_tenant_instagram_user_id",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "instagram_user_id",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "instagram_username",
                schema: "customers",
                table: "customers");
        }
    }
}
