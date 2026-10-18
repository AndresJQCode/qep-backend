using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Integrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShopifyProvider : Migration
    {
        /// <summary>D10 del spec 2026-10-08: proveedor nuevo = migración del CHECK, a propósito (spec 2026-10-15 §4.1).</summary>
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
                sql: "provider_key IN ('zenvia','whatsapp-cloud','instagram','shopify')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver atrás descarta las conexiones de shopify (y sus secretos y rutas, por el CASCADE): Postgres valida las
            // filas existentes al agregar el CHECK, y con una tienda conectada el rollback moriría con 23514 sin explicar
            // nada. Mismo criterio que el Down de AddInstagramProvider.
            migrationBuilder.Sql("DELETE FROM integrations.connections WHERE provider_key = 'shopify';");

            migrationBuilder.DropCheckConstraint(
                name: "CK_connections_provider_key",
                schema: "integrations",
                table: "connections");

            migrationBuilder.AddCheckConstraint(
                name: "CK_connections_provider_key",
                schema: "integrations",
                table: "connections",
                sql: "provider_key IN ('zenvia','whatsapp-cloud','instagram')");
        }
    }
}
