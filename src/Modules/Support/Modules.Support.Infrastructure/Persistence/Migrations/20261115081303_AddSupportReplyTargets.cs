using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportReplyTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ticket_messages_conversation",
                schema: "support",
                table: "ticket_messages");

            migrationBuilder.AddColumn<string>(
                name: "file_name",
                schema: "support",
                table: "ticket_replies",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_wa_id",
                schema: "support",
                table: "ticket_replies",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "upload_id",
                schema: "support",
                table: "ticket_replies",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_messages_conversation",
                schema: "support",
                table: "ticket_messages",
                columns: new[] { "tenant_id", "conversation_id", "message_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ticket_messages_conversation",
                schema: "support",
                table: "ticket_messages");

            migrationBuilder.DropColumn(
                name: "file_name",
                schema: "support",
                table: "ticket_replies");

            migrationBuilder.DropColumn(
                name: "target_wa_id",
                schema: "support",
                table: "ticket_replies");

            migrationBuilder.DropColumn(
                name: "upload_id",
                schema: "support",
                table: "ticket_replies");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_messages_conversation",
                schema: "support",
                table: "ticket_messages",
                columns: new[] { "tenant_id", "conversation_id" });
        }
    }
}
