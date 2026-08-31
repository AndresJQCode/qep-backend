using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Companies.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // FK a geography.cities(id) — otro schema, de otro modulo, de otro DbContext.
    // CompaniesDbContext no la modela: EF Core no soporta una relacion hacia una entidad que no
    // esta en el mismo ModelBuilder, asi que esta FK se agrega a mano con
    // migrationBuilder.AddForeignKey en vez de con HasOne<City>() en el modelo. Postgres la
    // impone igual; simplemente no aparece en el snapshot de EF ni se vuelve a tocar en la
    // proxima "dotnet ef migrations add". Mismo criterio que
    // AddCustomerCityAndClassification.cs en Customers.
    //
    // Requiere que geography.cities ya exista cuando esta migracion corre: Program.cs
    // inicializa Geography antes que Companies a proposito (ver el comentario ahi).
    public partial class AddCompanyCity : Migration
    {
        private const string CityForeignKey = "FK_companies_cities_city_id";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "city_id",
                schema: "companies",
                table: "companies",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill de las filas que ya existían antes de esta migración: el default de arriba
            // deja un GUID que ninguna ciudad tiene, y la FK de abajo lo rechazaría. Bogotá, D.C.
            // (Divipola 11001) es el mejor palo neutral disponible — no hay un valor "sin ciudad"
            // razonable para una empresa real. Sin filas previas (`companies.companies` vacía) esto
            // no actualiza nada.
            migrationBuilder.Sql(
                """
                UPDATE companies.companies
                SET city_id = (SELECT id FROM geography.cities WHERE divipola_code = '11001')
                WHERE city_id = '00000000-0000-0000-0000-000000000000'
                """);

            migrationBuilder.CreateIndex(
                name: "IX_companies_city",
                schema: "companies",
                table: "companies",
                column: "city_id");

            migrationBuilder.AddForeignKey(
                name: CityForeignKey,
                schema: "companies",
                table: "companies",
                column: "city_id",
                principalSchema: "geography",
                principalTable: "cities",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: CityForeignKey,
                schema: "companies",
                table: "companies");

            migrationBuilder.DropIndex(
                name: "IX_companies_city",
                schema: "companies",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "city_id",
                schema: "companies",
                table: "companies");
        }
    }
}
