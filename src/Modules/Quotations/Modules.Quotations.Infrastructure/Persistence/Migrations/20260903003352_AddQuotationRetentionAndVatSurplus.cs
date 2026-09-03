using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuotationRetentionAndVatSurplus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "customer_vat_surplus",
                schema: "quotations",
                table: "quotations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "customer_with_retention",
                schema: "quotations",
                table: "quotations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "net_total",
                schema: "quotations",
                table: "quotations",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "retention_amount",
                schema: "quotations",
                table: "quotations",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // QuotationStatus se achica a Draft/Sent/Voided/Expired -- no hay estado
            // "aprobada"/"convertida" (convertir a venta deja la cotizacion en Sent; la Sale
            // creada, con su QuotationId 1:1, es la senal de que ya se convirtio). Status se
            // guarda como texto (HasConversion<string>()), asi que una fila existente con
            // 'Approved' rompe al leerla apenas el modelo deja de reconocer ese valor -- Sent es
            // el dato mas cercano a la realidad (esa cotizacion ya se habia enviado) y sigue
            // siendo recuperable via Sale.QuotationId.
            migrationBuilder.Sql(
                "UPDATE quotations.quotations SET status = 'Sent' WHERE status = 'Approved';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // El UPDATE de status='Approved' -> 'Sent' de Up() no se deshace: no hay forma de
            // saber cuales de las filas ahora en Sent eran Approved antes de esta migracion.

            migrationBuilder.DropColumn(
                name: "customer_vat_surplus",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "customer_with_retention",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "net_total",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "retention_amount",
                schema: "quotations",
                table: "quotations");
        }
    }
}
