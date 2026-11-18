using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Integrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenAiProvider : Migration
    {
        /// <summary>D10 del spec 2026-10-08: proveedor nuevo = migración del CHECK, a propósito (spec 2026-10-29 §4.5, Tarea 6 del plan
        /// 2026-11-12).</summary>
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
                sql: "provider_key IN ('zenvia','whatsapp-cloud','instagram','shopify','external-api','anthropic','openai')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver atrás descarta las conexiones de openai (y sus secretos, por el CASCADE): Postgres valida las filas existentes al
            // agregar el CHECK y el rollback moriría con 23514. Mismo criterio que AddAnthropicProvider.
            migrationBuilder.Sql("DELETE FROM integrations.connections WHERE provider_key = 'openai';");

            migrationBuilder.DropCheckConstraint(
                name: "CK_connections_provider_key",
                schema: "integrations",
                table: "connections");

            migrationBuilder.AddCheckConstraint(
                name: "CK_connections_provider_key",
                schema: "integrations",
                table: "connections",
                sql: "provider_key IN ('zenvia','whatsapp-cloud','instagram','shopify','external-api','anthropic')");
        }
    }
}
