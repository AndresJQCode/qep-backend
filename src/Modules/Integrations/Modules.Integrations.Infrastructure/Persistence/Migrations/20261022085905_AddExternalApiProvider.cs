using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Integrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalApiProvider : Migration
    {
        /// <summary>D10 del spec 2026-10-08: proveedor nuevo = migración del CHECK, a propósito (spec 2026-10-21 D-H1).</summary>
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
                sql: "provider_key IN ('zenvia','whatsapp-cloud','instagram','shopify','external-api')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver atrás descarta las conexiones de external-api (y sus secretos, por el CASCADE): Postgres valida las filas
            // existentes al agregar el CHECK y el rollback moriría con 23514. Mismo criterio que AddShopifyProvider.
            migrationBuilder.Sql("DELETE FROM integrations.connections WHERE provider_key = 'external-api';");

            migrationBuilder.DropCheckConstraint(
                name: "CK_connections_provider_key",
                schema: "integrations",
                table: "connections");

            migrationBuilder.AddCheckConstraint(
                name: "CK_connections_provider_key",
                schema: "integrations",
                table: "connections",
                sql: "provider_key IN ('zenvia','whatsapp-cloud','instagram','shopify')");
        }
    }
}
