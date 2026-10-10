using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <summary>Spec 2026-10-09 §6.5. La columna nace nula; CustomerPhoneBackfillWorker la llena al arrancar.</summary>
    public partial class AddCustomerPhoneE164 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "phone_e164",
                schema: "customers",
                table: "customers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_customers_tenant_phone_e164",
                schema: "customers",
                table: "customers",
                columns: new[] { "tenant_id", "phone_e164" },
                filter: "phone_e164 IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_customers_tenant_phone_e164",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "phone_e164",
                schema: "customers",
                table: "customers");
        }
    }
}
