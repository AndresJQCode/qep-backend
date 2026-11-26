using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Siigo.Infrastructure.Persistence.Migrations
{
    /// <summary>Spec 2026-11-25 §7: aditiva. Sólo cambia el filtro de <c>IX_invoices_tenant_order_kind</c> para que una
    /// <c>Annulled</c> no sea vigente. Sin filas <c>Annulled</c> el índice nuevo tiene las mismas filas que el viejo, así que el código
    /// anterior corre contra este esquema; no hay relleno de datos.</summary>
    public partial class AddSiigoInvoiceAnnulled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_invoices_tenant_order_kind",
                schema: "siigo",
                table: "invoices");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_tenant_order_kind",
                schema: "siigo",
                table: "invoices",
                columns: new[] { "tenant_id", "order_id", "kind" },
                unique: true,
                filter: "status NOT IN ('Rejected', 'Failed', 'Annulled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No se corre: registro legal. Con una fila Annulled y la factura nueva del mismo pedido, el filtro viejo ni siquiera se
            // puede crear.
            migrationBuilder.DropIndex(
                name: "IX_invoices_tenant_order_kind",
                schema: "siigo",
                table: "invoices");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_tenant_order_kind",
                schema: "siigo",
                table: "invoices",
                columns: new[] { "tenant_id", "order_id", "kind" },
                unique: true,
                filter: "status NOT IN ('Rejected', 'Failed')");
        }
    }
}
