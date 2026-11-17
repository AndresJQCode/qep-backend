using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Companies.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyWithholdingTaxRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "company_withholding_tax_rates",
                schema: "companies",
                columns: table => new
                {
                    tax_rate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_withholding_tax_rates", x => new { x.company_id, x.tax_rate_id });
                    table.ForeignKey(
                        name: "FK_company_withholding_tax_rates_companies_company_id",
                        column: x => x.company_id,
                        principalSchema: "companies",
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_company_withholding_tax_rates_tenant_tax_rate",
                schema: "companies",
                table: "company_withholding_tax_rates",
                columns: new[] { "tenant_id", "tax_rate_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "company_withholding_tax_rates",
                schema: "companies");
        }
    }
}
