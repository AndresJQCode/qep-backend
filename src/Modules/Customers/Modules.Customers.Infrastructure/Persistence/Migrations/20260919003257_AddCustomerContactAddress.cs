using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// El domicilio vuelve al cliente (spec 2026-09-18): <c>customers.address</c> y
    /// <c>customers.city_id</c>, que <c>AddCustomerAddresses</c> había movido a la fila principal
    /// de la libreta. Desde entonces marcar otra dirección como principal cambiaba dónde está el
    /// cliente, y el siguiente PUT de la ficha pisaba esa dirección.
    ///
    /// El orden no es el que scaffoldea EF: las columnas nacen con default/nulas, se rellenan
    /// desde la principal, y recién entonces se vuelven obligatorias. Ningún cliente cambia de
    /// domicilio visible al desplegar. Si algún cliente no tiene principal, el <c>SET NOT NULL</c>
    /// falla y la migración no aplica: es un dato roto que hay que mirar, no un default. Todo va
    /// en una sola transacción (Npgsql hace DDL transaccional), así que un fallo a mitad no deja
    /// columnas a medio llenar.
    /// </summary>
    public partial class AddCustomerContactAddress : Migration
    {
        // La FK real hacia geography.cities se declara a mano, igual que la de customer_addresses:
        // City vive en otro DbContext y EF no modela relaciones fuera de su ModelBuilder. Postgres
        // la impone igual. Requiere que geography.cities ya exista cuando esta migración corre —
        // Program.cs inicializa Geography antes que Customers.
        private const string CityForeignKey = "FK_customers_cities_city_id";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Las columnas, todavía sin exigir nada: address con default vacío y city_id nula.
            migrationBuilder.AddColumn<string>(
                name: "address",
                schema: "customers",
                table: "customers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "city_id",
                schema: "customers",
                table: "customers",
                type: "uuid",
                nullable: true);

            // 2. El backfill desde la principal. Todo cliente tiene exactamente una
            // (Customer.ApplyPrincipal); si alguno no, city_id queda nula y el paso 3 lo denuncia.
            migrationBuilder.Sql(@"
                UPDATE customers.customers c
                SET address = a.address,
                    city_id = a.city_id
                FROM customers.customer_addresses a
                WHERE a.customer_id = c.id AND a.is_principal;");

            // 3. Ahora sí obligatorias, y sin el default que sólo servía para crear la columna.
            migrationBuilder.Sql("ALTER TABLE customers.customers ALTER COLUMN city_id SET NOT NULL;");
            migrationBuilder.Sql("ALTER TABLE customers.customers ALTER COLUMN address DROP DEFAULT;");

            // 4. La FK y el índice, con los nombres que tenían antes de CLI-DIR-01.
            migrationBuilder.AddForeignKey(
                name: CityForeignKey,
                schema: "customers",
                table: "customers",
                column: "city_id",
                principalSchema: "geography",
                principalTable: "cities",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateIndex(
                name: "IX_customers_city",
                schema: "customers",
                table: "customers",
                column: "city_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No hay que devolver nada a la libreta: nunca se sacó de ahí.
            migrationBuilder.DropForeignKey(
                name: CityForeignKey,
                schema: "customers",
                table: "customers");

            migrationBuilder.DropIndex(
                name: "IX_customers_city",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "address",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "city_id",
                schema: "customers",
                table: "customers");
        }
    }
}
