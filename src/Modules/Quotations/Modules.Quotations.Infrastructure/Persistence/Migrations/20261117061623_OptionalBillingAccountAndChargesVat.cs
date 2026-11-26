using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec 2026-11-17 (R1, R3, R5): banco y número del pedido pasan a nullable (empresa sin cuenta), el IVA de la empresa
    /// se congela en cotizaciones (nullable: el owned es opcional, rellenado en true donde hay empresa) y pedidos
    /// (<c>NOT NULL DEFAULT true</c>), y las líneas guardan la tasa del producto en <c>product_tax_percentage</c>, sin
    /// relleno (una fila vieja se lee con su <c>tax_percentage</c>).
    ///
    /// <b>No aditiva para el código viejo (R5):</b> desplegar con <c>Recreate</c> o el backend a cero réplicas. Un pod viejo
    /// leería un pedido sin cuenta y el Excel del ERP caería en su <c>!</c>. El <c>Down</c> falla si ya hay pedidos sin
    /// cuenta: no se corre después de emitir uno.
    /// </summary>
    public partial class OptionalBillingAccountAndChargesVat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "billing_company_charges_vat",
                schema: "quotations",
                table: "quotations",
                type: "boolean",
                nullable: true);

            // Sin esto EF no puede materializar el bool desde NULL en las cotizaciones que ya tienen empresa: todas
            // cobraban IVA hasta hoy.
            migrationBuilder.Sql(
                "UPDATE quotations.quotations SET billing_company_charges_vat = true WHERE billing_company_id IS NOT NULL;");

            migrationBuilder.AddColumn<int>(
                name: "product_tax_percentage",
                schema: "quotations",
                table: "quotation_items",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "billing_bank_name",
                schema: "quotations",
                table: "orders",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(120)",
                oldMaxLength: 120);

            migrationBuilder.AlterColumn<string>(
                name: "billing_account_number",
                schema: "quotations",
                table: "orders",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AddColumn<bool>(
                name: "billing_company_charges_vat",
                schema: "quotations",
                table: "orders",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "product_tax_percentage",
                schema: "quotations",
                table: "order_items",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "billing_company_charges_vat",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "product_tax_percentage",
                schema: "quotations",
                table: "quotation_items");

            migrationBuilder.DropColumn(
                name: "billing_company_charges_vat",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "product_tax_percentage",
                schema: "quotations",
                table: "order_items");

            migrationBuilder.AlterColumn<string>(
                name: "billing_bank_name",
                schema: "quotations",
                table: "orders",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(120)",
                oldMaxLength: 120,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "billing_account_number",
                schema: "quotations",
                table: "orders",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);
        }
    }
}
