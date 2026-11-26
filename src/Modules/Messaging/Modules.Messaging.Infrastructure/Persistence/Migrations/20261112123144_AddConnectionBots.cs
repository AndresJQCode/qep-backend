using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionBots : Migration
    {
        /// <summary>Spec 2026-10-29 §4.3. Respaldo: una fila por cada conexión que hoy atiende un chatbot publicado
        /// (<c>automations.chatbot_connections</c> con <c>active</c>). Mensajería migra antes que Automations: en una base nueva el
        /// esquema <c>automations</c> todavía no existe y no hay nada que copiar, por eso la guarda de <c>to_regclass</c>. Después, cada
        /// guardado de un chatbot la mantiene al día en su misma transacción (<c>ChatbotConnectionSlots</c>).</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "connection_bots",
                schema: "messaging",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bot_kind = table.Column<short>(type: "smallint", nullable: false),
                    bot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bot_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    reserved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connection_bots", x => x.connection_id);
                    table.CheckConstraint("CK_connection_bots_kind", "bot_kind IN (1,2)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_connection_bots_tenant_bot",
                schema: "messaging",
                table: "connection_bots",
                columns: new[] { "tenant_id", "bot_kind", "bot_id" });

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF to_regclass('automations.chatbot_connections') IS NOT NULL THEN
                        INSERT INTO messaging.connection_bots (connection_id, tenant_id, bot_kind, bot_id, bot_name, reserved_at)
                        SELECT cc.connection_id, cc.tenant_id, 1, c.id, c.name, now()
                        FROM automations.chatbot_connections cc
                        JOIN automations.chatbots c ON c.id = cc.chatbot_id
                        WHERE cc.active
                        ON CONFLICT (connection_id) DO NOTHING;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "connection_bots",
                schema: "messaging");
        }
    }
}
