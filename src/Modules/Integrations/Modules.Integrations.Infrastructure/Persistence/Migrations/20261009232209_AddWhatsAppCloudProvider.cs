using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Integrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppCloudProvider : Migration
    {
        /// <summary>D10 del spec 2026-10-08: proveedor nuevo = migración del CHECK, a propósito.</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_connections_provider_key",
                schema: "integrations",
                table: "connections");

            migrationBuilder.AddCheckConstraint(
                name: "CK_connections_provider_key",
                schema: "integrations",
                table: "connections",
                sql: "provider_key IN ('zenvia','whatsapp-cloud')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver atrás descarta las conexiones de whatsapp-cloud (y sus secretos, por el CASCADE):
            // Postgres valida las filas existentes al agregar el CHECK de un solo proveedor, y con una
            // conexión de Meta el rollback moriría con 23514 sin explicar nada. Mismo criterio que el
            // Down de AddMessagingModuleKey en Tenancy.
            migrationBuilder.Sql("DELETE FROM integrations.connections WHERE provider_key = 'whatsapp-cloud';");

            migrationBuilder.DropCheckConstraint(
                name: "CK_connections_provider_key",
                schema: "integrations",
                table: "connections");

            migrationBuilder.AddCheckConstraint(
                name: "CK_connections_provider_key",
                schema: "integrations",
                table: "connections",
                sql: "provider_key IN ('zenvia')");
        }
    }
}
