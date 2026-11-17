using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBotDelegations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_automation_inbox_kind",
                schema: "messaging",
                table: "automation_inbox");

            migrationBuilder.AddColumn<Guid>(
                name: "automation_delegation_id",
                schema: "messaging",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "delegation_id",
                schema: "messaging",
                table: "automation_inbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "bot_delegations",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chatbot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_inbound_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    brief = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bot_delegations", x => x.id);
                    table.CheckConstraint("CK_bot_delegations_result", "(status = 'Returned') = (result IS NOT NULL)");
                    table.CheckConstraint("CK_bot_delegations_status", "status IN ('Live','Returned','Interrupted')");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_delegation",
                schema: "messaging",
                table: "conversations",
                sql: "automation_delegation_id IS NULL OR (automation_status = 'Active' AND automation_bot_kind = 2)");

            migrationBuilder.CreateIndex(
                name: "IX_automation_inbox_delegation",
                schema: "messaging",
                table: "automation_inbox",
                columns: new[] { "delegation_id", "kind" },
                unique: true,
                filter: "delegation_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_automation_inbox_delegation",
                schema: "messaging",
                table: "automation_inbox",
                sql: "(kind IN ('Delegated','AgentReturned')) = (delegation_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_automation_inbox_kind",
                schema: "messaging",
                table: "automation_inbox",
                sql: "kind IN ('Engine','ReleaseRun','Delegated','AgentReturned')");

            migrationBuilder.CreateIndex(
                name: "IX_bot_delegations_live",
                schema: "messaging",
                table: "bot_delegations",
                column: "conversation_id",
                unique: true,
                filter: "status = 'Live'");

            migrationBuilder.CreateIndex(
                name: "IX_bot_delegations_tenant_conversation",
                schema: "messaging",
                table: "bot_delegations",
                columns: new[] { "tenant_id", "conversation_id" });
        }

        /// <summary>
        /// Borra la tabla, la columna de la conversación, los CHECK e índices nuevos y las filas de la cola de la delegación (sin borrarlas
        /// el CHECK viejo de <c>kind</c> no vuelve). <b>Sólo sobre una base sin delegaciones vivas:</b> con una conversación delegada deja
        /// <c>automation_bot_kind = 2</c> sin agente que vuelva al chatbot. En producción el rollback es revertir el código con el módulo
        /// <c>ai</c> apagado, no correr esto.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bot_delegations",
                schema: "messaging");

            migrationBuilder.DropCheckConstraint(
                name: "CK_conversations_delegation",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "IX_automation_inbox_delegation",
                schema: "messaging",
                table: "automation_inbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_automation_inbox_delegation",
                schema: "messaging",
                table: "automation_inbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_automation_inbox_kind",
                schema: "messaging",
                table: "automation_inbox");

            migrationBuilder.DropColumn(
                name: "automation_delegation_id",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "delegation_id",
                schema: "messaging",
                table: "automation_inbox");

            migrationBuilder.Sql("DELETE FROM messaging.automation_inbox WHERE kind IN ('Delegated','AgentReturned');");

            migrationBuilder.AddCheckConstraint(
                name: "CK_automation_inbox_kind",
                schema: "messaging",
                table: "automation_inbox",
                sql: "kind IN ('Engine','ReleaseRun')");
        }
    }
}
