using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DetachDataConsentsFromCustomers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // La FK no está en el modelo: la agregó a mano AddCustomerDataConsents, así que EF no la ve y hay que quitarla por
            // nombre. El consentimiento es prueba legal y sobrevive al cliente borrado; las dos columnas guardan su foto.
            migrationBuilder.DropForeignKey(
                name: "FK_data_consents_customers_customer_id",
                schema: "customers",
                table: "data_consents");

            migrationBuilder.AddColumn<string>(
                name: "customer_name",
                schema: "customers",
                table: "data_consents",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "customer_phone_e164",
                schema: "customers",
                table: "data_consents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Falla si ya hay consentimientos de clientes borrados: la FK no puede volver a crearse sobre filas huérfanas.
            // Nunca se corre hacia atrás (mismo criterio que AddCustomerDataConsents).
            migrationBuilder.DropColumn(
                name: "customer_name",
                schema: "customers",
                table: "data_consents");

            migrationBuilder.DropColumn(
                name: "customer_phone_e164",
                schema: "customers",
                table: "data_consents");

            migrationBuilder.AddForeignKey(
                name: "FK_data_consents_customers_customer_id",
                schema: "customers",
                table: "data_consents",
                column: "customer_id",
                principalSchema: "customers",
                principalTable: "customers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}