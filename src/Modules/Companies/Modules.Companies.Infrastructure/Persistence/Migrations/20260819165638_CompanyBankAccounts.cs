using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modules.Companies.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// EMP-08: el numero de cuenta plano de la empresa pasa a ser una coleccion de cuentas
    /// bancarias (banco, numero, moneda).
    ///
    /// El scaffold de EF salio en un orden que **pierde datos**: soltaba la columna antes de crear
    /// la tabla, con lo que no quedaba de donde copiar. Aca esta reordenado —crear, copiar,
    /// soltar— y con el INSERT de traspaso que EF no puede inferir. Es la unica parte escrita a
    /// mano; el resto es el scaffold tal cual.
    /// </summary>
    public partial class CompanyBankAccounts : Migration
    {
        /// <summary>
        /// Lo que se guarda como banco de una cuenta que ya existia. La columna vieja solo tenia el
        /// numero: ni el banco ni la moneda estaban en ningun lado, asi que no hay dato real que
        /// traspasar y cualquier valor concreto seria inventado.
        ///
        /// El marcador es deliberadamente visible en la UI en vez de una cadena vacia o un NULL:
        /// bank_name es NOT NULL, y una empresa migrada tiene que **verse** incompleta para que
        /// alguien la corrija. Un vacio se lee como "ya esta".
        /// </summary>
        private const string UnknownBank = "(banco sin especificar)";

        /// <summary>
        /// La moneda de una cuenta que ya existia. COP es una asuncion, y es la unica de esta
        /// migracion: el producto es colombiano —NIT, los datos de prueba del modulo— pero nada en
        /// los requisitos fija la moneda por defecto. Si al aplicarla hay filas de otra moneda, se
        /// corrigen a mano; el marcador del banco las hace faciles de encontrar.
        /// </summary>
        private const string AssumedCurrency = "COP";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. La tabla primero, para tener a donde copiar.
            migrationBuilder.CreateTable(
                name: "company_bank_accounts",
                schema: "companies",
                columns: table => new
                {
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    bank_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    account_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_bank_accounts", x => new { x.company_id, x.ordinal });
                    table.ForeignKey(
                        name: "FK_company_bank_accounts_companies_company_id",
                        column: x => x.company_id,
                        principalSchema: "companies",
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_company_bank_accounts_account_number",
                schema: "companies",
                table: "company_bank_accounts",
                column: "account_number");

            // 2. Una cuenta por empresa existente, con el numero que ya tenia. Sin esto, aplicar la
            //    migracion sobre una base con datos deja cada empresa con cero cuentas — un estado
            //    que el dominio considera invalido y que ningun PUT posterior podria explicar.
            //
            //    ordinal no se lista: es IDENTITY BY DEFAULT y lo asigna PostgreSQL.
            migrationBuilder.Sql(
                $"""
                INSERT INTO companies.company_bank_accounts (company_id, bank_name, account_number, currency)
                SELECT id, '{UnknownBank}', account_number, '{AssumedCurrency}'
                FROM companies.companies;
                """);

            // 3. Recien ahora se suelta el original.
            migrationBuilder.DropIndex(
                name: "IX_companies_tenant_account_number",
                schema: "companies",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "account_number",
                schema: "companies",
                table: "companies");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // La vuelta es parcial por construccion, y no puede no serlo: la columna guarda **un**
            // numero y la tabla puede tener varios por empresa. Se conserva el de menor ordinal
            // —la primera cargada— y el resto se pierde con la tabla.
            migrationBuilder.AddColumn<string>(
                name: "account_number",
                schema: "companies",
                table: "companies",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                """
                UPDATE companies.companies AS c
                SET account_number = a.account_number
                FROM (
                    SELECT DISTINCT ON (company_id) company_id, account_number
                    FROM companies.company_bank_accounts
                    ORDER BY company_id, ordinal
                ) AS a
                WHERE a.company_id = c.id;
                """);

            migrationBuilder.DropTable(
                name: "company_bank_accounts",
                schema: "companies");

            // Puede fallar, y esta bien que falle: el indice es unico y desde EMP-08 dos empresas
            // pueden compartir numero de cuenta legitimamente. Revertir sobre datos creados despues
            // de EMP-08 exige resolver esos duplicados a mano — que es exactamente la informacion
            // que hace falta para decidir, no un detalle que la migracion deba tapar eligiendo por
            // su cuenta a quien le cambia el numero.
            migrationBuilder.CreateIndex(
                name: "IX_companies_tenant_account_number",
                schema: "companies",
                table: "companies",
                columns: new[] { "tenant_id", "account_number" },
                unique: true);
        }
    }
}
