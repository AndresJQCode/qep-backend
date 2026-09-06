using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// La moneda en la que esta expresada la cotizacion entera.
    ///
    /// La fija la cuenta de cobro: una cuenta en dolares hace que toda la cotizacion se exprese
    /// en dolares, con el precio en dolares de cada producto (catalog.products.price_base_usd).
    /// No hay conversion en ningun punto — no existe tabla de cambio y este modulo no la inventa.
    ///
    /// Todo lo que ya existe nace en COP: hasta ahora US-5 decia que cada valor monetario del
    /// modulo era COP, y esas cotizaciones se cotizaron asi. Por eso el default es 'COP' y no
    /// una cadena vacia, que al leerla romperia el conversor del enum.
    /// </summary>
    public partial class AddQuotationCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "currency",
                schema: "quotations",
                table: "quotations",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "COP");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "currency",
                schema: "quotations",
                table: "quotations");
        }
    }
}
