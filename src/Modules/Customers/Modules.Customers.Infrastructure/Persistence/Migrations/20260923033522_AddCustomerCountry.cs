using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// El pais del cliente, y con el la ciudad en dos carriles.
    ///
    /// Hasta aca todo cliente era colombiano por construccion: su domicilio se ubicaba con
    /// <c>city_id</c>, una FK a <c>geography.cities</c>, que es DIVIPOLA — el estandar colombiano.
    /// Un cliente de Madrid no tiene fila ahi, asi que exigirle una era exigirle un dato que no
    /// existe.
    ///
    /// Desde aca <c>country</c> (ISO-3166-1 alpha-2) decide cual de los dos carriles se usa:
    /// <c>CO</c> ⇒ <c>city_id</c>; cualquier otro ⇒ <c>city_name</c>, texto libre. Nunca los dos —
    /// eso lo hace cumplir <c>Customer.EnsureValidLocation</c> y no una CHECK: la coherencia
    /// depende del pais, y expresarla en base obligaria a mantener la misma regla en dos lugares.
    ///
    /// El backfill es <c>CO</c> para todo el padron existente, que es lo que era: cada fila tiene
    /// un <c>city_id</c> que apunta a una ciudad colombiana.
    /// </summary>
    public partial class AddCustomerCountry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. city_id deja de ser obligatoria. La FK (FK_customers_cities_city_id) sigue valiendo
            //    tal cual: Postgres no aplica una FK sobre un NULL, asi que un cliente de afuera con
            //    city_id nula no la viola y no hay que tocarla.
            migrationBuilder.Sql(
                "ALTER TABLE customers.customers ALTER COLUMN city_id DROP NOT NULL;");

            // 2. La ciudad escrita a mano del cliente de afuera. Nula para uno colombiano.
            migrationBuilder.AddColumn<string>(
                name: "city_name",
                schema: "customers",
                table: "customers",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            // 3. El pais, todavia sin exigir nada: la columna nace nula para poder crearse sobre
            //    las filas que ya estan.
            migrationBuilder.AddColumn<string>(
                name: "country",
                schema: "customers",
                table: "customers",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            // 4. El backfill. Todo el padron anterior es colombiano: su city_id apunta a
            //    geography.cities, que solo tiene ciudades de Colombia.
            migrationBuilder.Sql(
                "UPDATE customers.customers SET country = 'CO' WHERE country IS NULL;");

            // 5. Ahora si obligatoria, y **sin** DEFAULT: un cliente nuevo tiene que decir de donde
            //    es. Un DEFAULT 'CO' esconderia un alta a la que se le olvido mandar el pais, y ese
            //    cliente quedaria en Colombia sin que nadie lo haya dicho.
            migrationBuilder.Sql(
                "ALTER TABLE customers.customers ALTER COLUMN country SET NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver atras solo es posible si no quedo ningun cliente de afuera: sin country ni
            // city_name esa fila pierde su ciudad para siempre, y ademas no pasa el NOT NULL de
            // abajo. Se frena con un mensaje que dice que hacer, igual que AddCustomerContactAddress
            // hace con el cliente sin direccion principal — mejor que un "null value violates
            // not-null constraint" que no explica nada.
            migrationBuilder.Sql(@"
                DO $$
                DECLARE foreign_count bigint;
                BEGIN
                    SELECT count(*) INTO foreign_count
                    FROM customers.customers
                    WHERE city_id IS NULL;

                    IF foreign_count > 0 THEN
                        RAISE EXCEPTION
                            'No se puede revertir AddCustomerCountry: hay % cliente(s) sin ciudad DIVIPOLA. Asignales una ciudad colombiana o borralos antes de revertir.',
                            foreign_count;
                    END IF;
                END $$;");

            migrationBuilder.DropColumn(
                name: "city_name",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "country",
                schema: "customers",
                table: "customers");

            migrationBuilder.Sql(
                "ALTER TABLE customers.customers ALTER COLUMN city_id SET NOT NULL;");
        }
    }
}
