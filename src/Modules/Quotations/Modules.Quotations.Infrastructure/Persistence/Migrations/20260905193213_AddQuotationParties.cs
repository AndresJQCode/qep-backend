using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Los cuatro `*_override` de `quotations` pasan a filas de `quotation_parties` (una por
    /// cotización y rol). El orden importa y no es el que scaffoldea EF: crear la tabla, mover lo
    /// que hay, y recién entonces borrar las columnas — al revés se pierden los datos.
    /// </summary>
    public partial class AddQuotationParties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "quotation_parties",
                schema: "quotations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    address = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    city_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotation_parties", x => x.id);
                    table.ForeignKey(
                        name: "FK_quotation_parties_quotations_quotation_id",
                        column: x => x.quotation_id,
                        principalSchema: "quotations",
                        principalTable: "quotations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_quotation_parties_quotation_role",
                schema: "quotations",
                table: "quotation_parties",
                columns: new[] { "quotation_id", "role" },
                unique: true);

            // Facturación: lo que había se copia tal cual. Sólo las cotizaciones que realmente
            // tenían algo escrito — una sin overrides no estrena fila, que es justamente lo que
            // ahora significa "factura a los datos del cliente".
            migrationBuilder.Sql(@"
                INSERT INTO quotations.quotation_parties (id, quotation_id, role, name, address)
                SELECT gen_random_uuid(), q.id, 'Billing', q.billing_name_override, q.billing_address_override
                FROM quotations.quotations q
                WHERE q.billing_name_override IS NOT NULL
                   OR q.billing_address_override IS NOT NULL;");

            // Entrega: `delivery_city_override` era texto libre y ahora la ciudad es un id de
            // `geography`, así que hay que resolverlo. Se mapea **sólo cuando el nombre identifica
            // una ciudad sin ambigüedad** (`HAVING count(*) = 1`): hay nombres repetidos entre
            // departamentos, y elegir uno al azar pondría una entrega en otra provincia. Lo que no
            // resuelve queda con `city_id` nulo — la cotización vuelve a mostrar la ciudad del
            // cliente para ese campo, que es el mismo comportamiento que tenía sin override.
            //
            // Es la única consulta del repo que cruza esquemas de dos módulos, y es a propósito:
            // mover el dato una vez, acá, es más barato y más auditable que una tarea de
            // aplicación que haga lo mismo. El guard de `to_regclass` es para la base nueva, donde
            // este módulo puede migrarse antes que `geography` (y donde, sin cotizaciones, no hay
            // nada que mover).
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF to_regclass('geography.cities') IS NULL THEN
                        INSERT INTO quotations.quotation_parties (id, quotation_id, role, address)
                        SELECT gen_random_uuid(), q.id, 'Shipping', q.delivery_address_override
                        FROM quotations.quotations q
                        WHERE q.delivery_address_override IS NOT NULL
                           OR q.delivery_city_override IS NOT NULL;
                    ELSE
                        INSERT INTO quotations.quotation_parties
                            (id, quotation_id, role, address, department_id, city_id)
                        SELECT gen_random_uuid(), q.id, 'Shipping', q.delivery_address_override,
                               match.department_id, match.city_id
                        FROM quotations.quotations q
                        LEFT JOIN LATERAL (
                            SELECT (array_agg(c.id))[1] AS city_id,
                                   (array_agg(c.department_id))[1] AS department_id
                            FROM geography.cities c
                            WHERE q.delivery_city_override IS NOT NULL
                              AND lower(c.name) = lower(btrim(q.delivery_city_override))
                            HAVING count(*) = 1
                        ) match ON true
                        WHERE q.delivery_address_override IS NOT NULL
                           OR q.delivery_city_override IS NOT NULL;
                    END IF;
                END $$;");

            migrationBuilder.DropColumn(
                name: "billing_address_override",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "billing_name_override",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "delivery_address_override",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "delivery_city_override",
                schema: "quotations",
                table: "quotations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "billing_address_override",
                schema: "quotations",
                table: "quotations",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billing_name_override",
                schema: "quotations",
                table: "quotations",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "delivery_address_override",
                schema: "quotations",
                table: "quotations",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "delivery_city_override",
                schema: "quotations",
                table: "quotations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // La vuelta atrás recupera lo que el modelo viejo sabía representar: nombre y dirección
            // de facturación, dirección y ciudad de entrega. Teléfono, email y departamento de cada
            // parte no tienen columna a dónde volver y se pierden — es la asimetría esperable al
            // desandar un modelo más chico, no un descuido.
            migrationBuilder.Sql(@"
                UPDATE quotations.quotations q
                SET billing_name_override = p.name,
                    billing_address_override = p.address
                FROM quotations.quotation_parties p
                WHERE p.quotation_id = q.id AND p.role = 'Billing';");

            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF to_regclass('geography.cities') IS NULL THEN
                        UPDATE quotations.quotations q
                        SET delivery_address_override = p.address
                        FROM quotations.quotation_parties p
                        WHERE p.quotation_id = q.id AND p.role = 'Shipping';
                    ELSE
                        UPDATE quotations.quotations q
                        SET delivery_address_override = p.address,
                            delivery_city_override = left(c.name, 100)
                        FROM quotations.quotation_parties p
                        LEFT JOIN geography.cities c ON c.id = p.city_id
                        WHERE p.quotation_id = q.id AND p.role = 'Shipping';
                    END IF;
                END $$;");

            migrationBuilder.DropTable(
                name: "quotation_parties",
                schema: "quotations");
        }
    }
}
