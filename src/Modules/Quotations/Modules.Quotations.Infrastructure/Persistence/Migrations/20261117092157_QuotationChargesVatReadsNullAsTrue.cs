using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sólo modelo, sin cambio de esquema (aditiva). <c>quotations.billing_company_charges_vat</c> pasa a mapearse por
    /// <c>QuotationBillingAccount.StoredChargesVat</c> (<c>bool?</c>), que lee <c>NULL</c> como <c>true</c>: un pod sin la
    /// columna deja <c>NULL</c> en una cotización con empresa, y materializar el <c>bool</c> desde ahí lanzaba.
    /// </summary>
    public partial class QuotationChargesVatReadsNullAsTrue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
