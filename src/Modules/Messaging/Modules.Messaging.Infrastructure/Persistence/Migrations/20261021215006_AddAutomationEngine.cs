using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "sent_by_chatbot_id",
                schema: "messaging",
                table: "messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "sent_by_kind",
                schema: "messaging",
                table: "messages",
                type: "smallint",
                nullable: false,
                defaultValue: (short)1);

            migrationBuilder.AddColumn<Guid>(
                name: "automation_chatbot_id",
                schema: "messaging",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "automation_step",
                schema: "messaging",
                table: "conversations",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "automation_inbox",
                schema: "messaging",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opened = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_inbox", x => x.message_id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_sent_by_chatbot",
                schema: "messaging",
                table: "messages",
                sql: "(sent_by_kind = 2) = (sent_by_chatbot_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_sent_by_kind",
                schema: "messaging",
                table: "messages",
                sql: "sent_by_kind IN (1,2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_sent_by_one",
                schema: "messaging",
                table: "messages",
                sql: "sent_by_kind <> 2 OR sent_by_member_id IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_automation_chatbot",
                schema: "messaging",
                table: "conversations",
                sql: "automation_status = 'None' OR automation_chatbot_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_automation_inbox_conversation",
                schema: "messaging",
                table: "automation_inbox",
                columns: new[] { "conversation_id", "created_at" },
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_automation_inbox_pending",
                schema: "messaging",
                table: "automation_inbox",
                column: "next_attempt_at",
                filter: "processed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_inbox",
                schema: "messaging");

            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_sent_by_chatbot",
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
                name: "CK_conversations_automation_chatbot",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "sent_by_chatbot_id",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "sent_by_kind",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "automation_chatbot_id",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "automation_step",
                schema: "messaging",
                table: "conversations");
        }
    }
}
