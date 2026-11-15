using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec ReteICA por empresa §7.5 y §11.1: el municipio de la empresa congelado con la cuenta y el municipio de la
    /// operación, en cotizaciones y pedidos. Nullables, sin FK y sin relleno (A8): un documento anterior completa el de la
    /// empresa la próxima vez que el cuerpo trae la cuenta, y el de la operación cuando alguien lo fija.
    /// </summary>
    public partial class AddOperationCity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "billing_company_city_id",
                schema: "quotations",
                table: "quotations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "operation_city_id",
                schema: "quotations",
                table: "quotations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "billing_company_city_id",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "operation_city_id",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "billing_company_city_id",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "operation_city_id",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "billing_company_city_id",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "operation_city_id",
                schema: "quotations",
                table: "orders");
        }
    }
}
