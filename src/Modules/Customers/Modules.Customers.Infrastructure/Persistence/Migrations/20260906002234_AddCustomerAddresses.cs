using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// El par <c>address</c>/<c>city_id</c> de <c>customers</c> pasa a ser la primera fila de
    /// <c>customer_addresses</c>, marcada como principal. El orden importa y no es el que
    /// scaffoldea EF: crear la tabla, mover lo que hay, y recién entonces borrar las columnas.
    ///
    /// Todo cliente existente estrena exactamente una dirección: <c>city_id</c> era obligatoria,
    /// así que ninguno queda sin principal — que es lo que el agregado (y la cotización, que
    /// propone esa dirección por defecto) dan por sentado.
    /// </summary>
    public partial class AddCustomerAddresses : Migration
    {
        // La FK real hacia geography.cities se declara a mano, igual que la que tenía
        // customers.city_id: City vive en otro DbContext y EF no modela relaciones fuera de su
        // ModelBuilder. Postgres la impone igual. Requiere que geography.cities ya exista cuando
        // esta migración corre — Program.cs inicializa Geography antes que Customers.
        private const string CityForeignKey = "FK_customer_addresses_cities_city_id";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_addresses",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    city_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_principal = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_addresses", x => x.id);
                    table.ForeignKey(
                        name: "FK_customer_addresses_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "customers",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_addresses_customer",
                schema: "customers",
                table: "customer_addresses",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_customer_addresses_city",
                schema: "customers",
                table: "customer_addresses",
                column: "city_id");

            // Índice **no** único, y es a propósito. Uno único parcial —lo natural para "una sola
            // principal por cliente"— rechaza el cambio legítimo de principal: Postgres valida el
            // índice sentencia por sentencia, EF manda las dos filas (la que se desmarca y la que
            // se marca) en un mismo lote y no promete el orden, así que con la desmarcada segunda
            // el índice ve dos principales a la vez y aborta. Postgres tampoco admite diferir un
            // índice parcial (sólo las constraints, que no pueden ser parciales).
            //
            // El invariante lo sostiene `Customer.ApplyPrincipal`, único camino de escritura. Si
            // se quiere garantía de base, la forma que sí la da es mover el puntero al cliente
            // (`customers.principal_address_id`): una columna con un solo valor no puede tener dos.
            migrationBuilder.CreateIndex(
                name: "IX_customer_addresses_principal",
                schema: "customers",
                table: "customer_addresses",
                columns: new[] { "customer_id", "is_principal" });

            migrationBuilder.AddForeignKey(
                name: CityForeignKey,
                schema: "customers",
                table: "customer_addresses",
                column: "city_id",
                principalSchema: "geography",
                principalTable: "cities",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // El traspaso: una principal por cliente con lo que ya tenía. El nombre es el del
            // cliente —"a quién pertenece" la dirección, y hasta hoy sólo había una— y el teléfono
            // el suyo. `address` era opcional y la columna nueva no lo es: un cliente sin dirección
            // escrita estrena la fila con la cadena vacía, que es lo que el formulario ya mostraba.
            migrationBuilder.Sql(@"
                INSERT INTO customers.customer_addresses
                    (id, customer_id, name, address, phone, city_id, is_principal,
                     created_at, updated_at)
                SELECT gen_random_uuid(), c.id, left(c.name, 120), coalesce(c.address, ''),
                       c.phone, c.city_id, true, c.created_at, c.updated_at
                FROM customers.customers c;");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "address",
                schema: "customers",
                table: "customers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "city_id",
                schema: "customers",
                table: "customers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Vuelve la principal a las columnas del cliente. Las demás direcciones se pierden: el
            // modelo viejo no tiene dónde ponerlas, y es la asimetría esperable al desandar hacia
            // un modelo más chico.
            migrationBuilder.Sql(@"
                UPDATE customers.customers c
                SET address = nullif(a.address, ''),
                    city_id = a.city_id
                FROM customers.customer_addresses a
                WHERE a.customer_id = c.id AND a.is_principal;");

            migrationBuilder.CreateIndex(
                name: "IX_customers_city",
                schema: "customers",
                table: "customers",
                column: "city_id");

            migrationBuilder.DropTable(
                name: "customer_addresses",
                schema: "customers");
        }
    }
}
