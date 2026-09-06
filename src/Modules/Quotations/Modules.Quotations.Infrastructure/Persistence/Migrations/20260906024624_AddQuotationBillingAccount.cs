using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Con que empresa y a que cuenta se cobra una cotizacion.
    ///
    /// Cuatro columnas en `quotations` y no una FK a la cuenta de la empresa: la cotizacion
    /// **copia** banco, numero y moneda al guardarse (ver QuotationBillingAccount). Una FK haria
    /// que corregir un digito en la ficha de la empresa reescribiera cotizaciones ya enviadas —y
    /// ademas CompanyBankAccount es un value object sin id propio, no hay a que apuntar—. El
    /// billing_company_id si queda, para poder decir "a nombre de quien" aunque el numero haya
    /// cambiado; sin FK, como toda referencia entre modulos.
    ///
    /// Las cuatro nacen nullable y sin backfill: las cotizaciones que ya existen no eligieron
    /// cuenta, y ninguna regla exige que la tengan.
    /// </summary>
    public partial class AddQuotationBillingAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "billing_account_currency",
                schema: "quotations",
                table: "quotations",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billing_account_number",
                schema: "quotations",
                table: "quotations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billing_bank_name",
                schema: "quotations",
                table: "quotations",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "billing_company_id",
                schema: "quotations",
                table: "quotations",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "billing_account_currency",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "billing_account_number",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "billing_bank_name",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "billing_company_id",
                schema: "quotations",
                table: "quotations");
        }
    }
}
