using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Companies.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCompaniesTrigramIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Los indices GIN de abajo dependen de los operadores que trae esta extension
            // (gin_trgm_ops). `IF NOT EXISTS`: el modulo Customers puede haberla habilitado ya
            // (AddCustomersNameTrigramIndex), pero este modulo no puede asumir el orden de
            // aplicacion de las migraciones de otro.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.DropIndex(
                name: "IX_company_bank_accounts_account_number",
                schema: "companies",
                table: "company_bank_accounts");

            migrationBuilder.CreateIndex(
                name: "IX_company_bank_accounts_account_number_trgm",
                schema: "companies",
                table: "company_bank_accounts",
                column: "account_number")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_companies_name_trgm",
                schema: "companies",
                table: "companies",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_company_bank_accounts_account_number_trgm",
                schema: "companies",
                table: "company_bank_accounts");

            migrationBuilder.DropIndex(
                name: "IX_companies_name_trgm",
                schema: "companies",
                table: "companies");

            migrationBuilder.CreateIndex(
                name: "IX_company_bank_accounts_account_number",
                schema: "companies",
                table: "company_bank_accounts",
                column: "account_number");
        }
    }
}
