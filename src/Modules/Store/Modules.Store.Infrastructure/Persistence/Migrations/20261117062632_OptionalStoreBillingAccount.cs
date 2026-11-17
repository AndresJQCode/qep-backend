using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Store.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec 2026-11-17 §4.3: <c>billing_bank_name</c> y <c>billing_account_number</c> de <c>store.store_currencies</c> pasan a
    /// nullable; null los dos es una empresa sin cuenta. <c>billing_currency</c> y su CHECK no cambian. No aditiva para el
    /// código viejo (lee esas columnas como texto obligatorio): el despliegue del spec 2026-11-17 va con corte (R5),
    /// <c>Recreate</c> o el backend a cero réplicas. El <c>Down</c> falla si ya hay una moneda sin cuenta: el rollback es
    /// revertir el código y dejar las columnas.
    /// </summary>
    public partial class OptionalStoreBillingAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "billing_bank_name",
                schema: "store",
                table: "store_currencies",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(120)",
                oldMaxLength: 120);

            migrationBuilder.AlterColumn<string>(
                name: "billing_account_number",
                schema: "store",
                table: "store_currencies",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "billing_bank_name",
                schema: "store",
                table: "store_currencies",
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
                schema: "store",
                table: "store_currencies",
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
