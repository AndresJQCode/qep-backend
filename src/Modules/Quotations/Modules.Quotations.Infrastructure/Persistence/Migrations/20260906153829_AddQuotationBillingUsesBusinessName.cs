using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Con los datos del cliente, a cual de sus dos nombres se le factura: el de contacto o la
    /// razon social (CLI-RS-01). Un cliente mayorista es una empresa y factura a su razon social,
    /// pero el contacto sigue siendo la persona con la que se habla.
    ///
    /// Default false y sin backfill: hasta ahora toda cotizacion facturo al unico nombre que el
    /// cliente tenia, que es el de contacto. Ponerlo en true cambiaria el nombre impreso en
    /// cotizaciones ya emitidas.
    /// </summary>
    public partial class AddQuotationBillingUsesBusinessName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "billing_uses_business_name",
                schema: "quotations",
                table: "quotations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "billing_uses_business_name",
                schema: "quotations",
                table: "quotations");
        }
    }
}
