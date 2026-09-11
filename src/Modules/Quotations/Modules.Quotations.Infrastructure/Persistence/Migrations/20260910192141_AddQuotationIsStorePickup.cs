using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// El cliente recoge el pedido en la tienda en vez de recibirlo en una dirección. Con esto
    /// prendido la cotización no tiene parte de entrega (<c>Quotation.IsStorePickup</c>).
    ///
    /// Default false y sin backfill: hasta ahora toda cotización se entregaba, a los datos del
    /// cliente o a su parte de envío. Ponerlo en true cambiaría la entrega impresa en
    /// cotizaciones ya emitidas.
    /// </summary>
    public partial class AddQuotationIsStorePickup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_store_pickup",
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
                name: "is_store_pickup",
                schema: "quotations",
                table: "quotations");
        }
    }
}
