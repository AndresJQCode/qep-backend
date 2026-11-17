using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_sent_by_kind",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_sent_by_one",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_conversations_automation_chatbot",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.AddColumn<Guid>(
                name: "sent_by_agent_id",
                schema: "messaging",
                table: "messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "automation_agent_id",
                schema: "messaging",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "automation_bot_kind",
                schema: "messaging",
                table: "conversations",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "bot_kind",
                schema: "messaging",
                table: "automation_inbox",
                type: "smallint",
                nullable: false,
                defaultValue: (short)1);

            // Spec 2026-10-29 §4.4: todo asistente que existía era un chatbot. Antes del CHECK nuevo, que exige el tipo fuera de None.
            migrationBuilder.Sql(
                "UPDATE messaging.conversations SET automation_bot_kind = 1 WHERE automation_status <> 'None' AND automation_bot_kind IS NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_sent_by_agent",
                schema: "messaging",
                table: "messages",
                sql: "(sent_by_kind = 3) = (sent_by_agent_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_sent_by_kind",
                schema: "messaging",
                table: "messages",
                sql: "sent_by_kind IN (1,2,3,4)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_sent_by_one",
                schema: "messaging",
                table: "messages",
                sql: "sent_by_kind = 1 OR sent_by_member_id IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_automation_bot",
                schema: "messaging",
                table: "conversations",
                sql: "automation_status = 'None' OR (automation_bot_kind = 1 AND automation_chatbot_id IS NOT NULL AND automation_agent_id IS NULL) OR (automation_bot_kind = 2 AND automation_agent_id IS NOT NULL AND automation_chatbot_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_automation_inbox_pending_kind",
                schema: "messaging",
                table: "automation_inbox",
                columns: new[] { "bot_kind", "next_attempt_at" },
                filter: "processed_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_automation_inbox_bot_kind",
                schema: "messaging",
                table: "automation_inbox",
                sql: "bot_kind IN (1,2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Sólo sobre una base sin agentes: con mensajes sent_by_kind = 3 o conversaciones de un agente, los CHECK viejos de abajo
            // fallan y el Down se deshace entero. El rollback de esta función es revertir el código, no correr este Down.
            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_sent_by_agent",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_sent_by_kind",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_sent_by_one",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_conversations_automation_bot",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "IX_automation_inbox_pending_kind",
                schema: "messaging",
                table: "automation_inbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_automation_inbox_bot_kind",
                schema: "messaging",
                table: "automation_inbox");

            migrationBuilder.DropColumn(
                name: "sent_by_agent_id",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "automation_agent_id",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "automation_bot_kind",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "bot_kind",
                schema: "messaging",
                table: "automation_inbox");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_sent_by_kind",
                schema: "messaging",
                table: "messages",
                sql: "sent_by_kind IN (1,2,4)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_sent_by_one",
                schema: "messaging",
                table: "messages",
                sql: "sent_by_kind NOT IN (2,4) OR sent_by_member_id IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_automation_chatbot",
                schema: "messaging",
                table: "conversations",
                sql: "automation_status = 'None' OR automation_chatbot_id IS NOT NULL");
        }
    }
}
