using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerCityAndClassification : Migration
    {
        // FK a geography.cities(id) — otro schema, de otro modulo, de otro DbContext.
        // CustomersDbContext no la modela (ver el comentario en
        // CustomersDbContext.ConfigureCustomer): EF Core no soporta una relacion hacia una entidad
        // que no esta en el mismo ModelBuilder, asi que esta FK se agrega a mano con
        // migrationBuilder.AddForeignKey en vez de con HasOne<City>() en el modelo. Postgres la
        // impone igual; simplemente no aparece en el snapshot de EF ni se vuelve a tocar en la
        // proxima "dotnet ef migrations add" porque el modelo nunca la declaro.
        //
        // Requiere que geography.cities ya exista cuando esta migracion corre: Program.cs
        // inicializa Geography antes que Customers a proposito (ver el comentario ahi).
        private const string CityForeignKey = "FK_customers_cities_city_id";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "city",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "classification",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "department",
                schema: "customers",
                table: "customers");

            migrationBuilder.AddColumn<Guid>(
                name: "city_id",
                schema: "customers",
                table: "customers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "classification_id",
                schema: "customers",
                table: "customers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddUniqueConstraint(
                name: "AK_client_classifications_tenant_id_id",
                schema: "customers",
                table: "client_classifications",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_customers_city",
                schema: "customers",
                table: "customers",
                column: "city_id");

            migrationBuilder.CreateIndex(
                name: "IX_customers_classification",
                schema: "customers",
                table: "customers",
                column: "classification_id");

            migrationBuilder.CreateIndex(
                name: "IX_customers_tenant_id_classification_id",
                schema: "customers",
                table: "customers",
                columns: new[] { "tenant_id", "classification_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_customers_client_classifications_classification_id",
                schema: "customers",
                table: "customers",
                columns: new[] { "tenant_id", "classification_id" },
                principalSchema: "customers",
                principalTable: "client_classifications",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: CityForeignKey,
                schema: "customers",
                table: "customers",
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
                schema: "customers",
                table: "customers");

            migrationBuilder.DropForeignKey(
                name: "FK_customers_client_classifications_classification_id",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropIndex(
                name: "IX_customers_city",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropIndex(
                name: "IX_customers_classification",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropIndex(
                name: "IX_customers_tenant_id_classification_id",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_client_classifications_tenant_id_id",
                schema: "customers",
                table: "client_classifications");

            migrationBuilder.DropColumn(
                name: "city_id",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "classification_id",
                schema: "customers",
                table: "customers");

            migrationBuilder.AddColumn<string>(
                name: "city",
                schema: "customers",
                table: "customers",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "classification",
                schema: "customers",
                table: "customers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "department",
                schema: "customers",
                table: "customers",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
        }
    }
}
