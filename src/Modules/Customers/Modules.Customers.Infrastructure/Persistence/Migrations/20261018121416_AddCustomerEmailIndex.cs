using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerEmailIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Spec de Shopify 2026-10-15 §9 (H8): la búsqueda de un comprador por correo sin mayúsculas
            // (CustomerRepository.FindIdByEmailAsync). Por SQL porque EF no modela índices por expresión; no es único:
            // el correo no identifica a un cliente (CustomersDbContext.EmailIndex).
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_customers_tenant_email\" ON customers.customers (tenant_id, lower(email));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS customers.\"IX_customers_tenant_email\";");
        }
    }
}
