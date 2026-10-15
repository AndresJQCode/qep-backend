using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInstagramChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_kind",
                schema: "messaging",
                table: "messages");

            migrationBuilder.AddColumn<string>(
                name: "provider",
                schema: "messaging",
                table: "webhook_deliveries",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "whatsapp");

            migrationBuilder.AddColumn<string>(
                name: "source_url",
                schema: "messaging",
                table: "message_media",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "channel",
                schema: "messaging",
                table: "conversations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "WhatsApp");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_kind",
                schema: "messaging",
                table: "messages",
                sql: "kind BETWEEN 1 AND 14");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_tenant_channel_status_activity",
                schema: "messaging",
                table: "conversations",
                columns: new[] { "tenant_id", "channel", "status", "last_activity_at", "id" },
                descending: new[] { false, false, false, true, true });

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_channel",
                schema: "messaging",
                table: "conversations",
                sql: "channel IN ('WhatsApp','Instagram')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_kind",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_conversations_tenant_channel_status_activity",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_conversations_channel",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "provider",
                schema: "messaging",
                table: "webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "source_url",
                schema: "messaging",
                table: "message_media");

            migrationBuilder.DropColumn(
                name: "channel",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_kind",
                schema: "messaging",
                table: "messages",
                sql: "kind BETWEEN 1 AND 13");
        }
    }
}
