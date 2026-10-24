using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerIdentificationNormalizedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Spec de clientes duplicados 2026-10-24 §3.2: la búsqueda por número de documento sin signos ni mayúsculas
            // (CustomerDuplicateQueries). Por SQL porque EF no modela índices por expresión. No es único: R2, un documento
            // repetido entre formatos se avisa, no se frena. La expresión es la de CustomerIdentificationSql.NormalizedNumber,
            // repetida literal porque una migración no lee constantes; TheMatchQueryUsesTheNormalizedIndex amarra que coincidan.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_customers_tenant_identification_normalized\" ON customers.customers " +
                "(tenant_id, upper(regexp_replace(identification_number, '[^0-9A-Za-z]', '', 'g'))) " +
                "WHERE identification_number IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS customers.\"IX_customers_tenant_identification_normalized\";");
        }
    }
}
