using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// La fecha del soporte de pago de cada comprobante (a pedido, 2026-10-08): la que escribe quien
    /// lo adjunta en el asistente y la que el Excel muestra en «Fecha Pago N».
    ///
    /// `date` y no `timestamp`: es un día, no un instante. Nullable y sin backfill: los comprobantes
    /// que ya existen no la trajeron, y derivarla de `uploaded_at` sería inventar un dato; para ellos
    /// la hoja sigue cayendo a la fecha de subida.
    /// </summary>
    public partial class AddOrderPaymentProofPaidOn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "paid_on",
                schema: "quotations",
                table: "order_payment_proofs",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "paid_on",
                schema: "quotations",
                table: "order_payment_proofs");
        }
    }
}
