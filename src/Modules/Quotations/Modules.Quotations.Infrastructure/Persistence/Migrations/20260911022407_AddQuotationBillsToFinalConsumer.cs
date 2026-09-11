using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// La factura sale a nombre de consumidor final (<c>Quotation.BillsToFinalConsumer</c>), con
    /// nombre y NIT fijos que no se guardan: son constantes de dominio (<c>FinalConsumer</c>).
    ///
    /// Default false y sin backfill: hasta ahora ninguna cotización podía facturar a consumidor
    /// final. Ponerlo en true cambiaría a quién se le facturó en cotizaciones ya emitidas, y
    /// además les apagaría la retención y el excedente de IVA.
    /// </summary>
    public partial class AddQuotationBillsToFinalConsumer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "bills_to_final_consumer",
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
                name: "bills_to_final_consumer",
                schema: "quotations",
                table: "quotations");
        }
    }
}
