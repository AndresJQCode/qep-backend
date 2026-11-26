using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "retry_disabled",
                schema: "messaging",
                table: "messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "scheduled_messages",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_voice_note = table.Column<bool>(type: "boolean", nullable: false),
                    text = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    caption = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    media_storage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    media_mime_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    media_file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    media_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    media_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    author_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_messages", x => x.id);
                    table.CheckConstraint("CK_scheduled_messages_media_shape", "kind = 'Text' OR media_mime_type IS NOT NULL");
                    table.CheckConstraint("CK_scheduled_messages_status", "status IN ('Scheduled','Sending','Sent','Failed','Cancelled')");
                    table.CheckConstraint("CK_scheduled_messages_text_shape", "kind <> 'Text' OR (text IS NOT NULL AND media_mime_type IS NULL)");
                    table.CheckConstraint("CK_scheduled_messages_voice_note", "NOT is_voice_note OR kind = 'Audio'");
                    table.ForeignKey(
                        name: "FK_scheduled_messages_conversation",
                        column: x => x.conversation_id,
                        principalSchema: "messaging",
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_messages_due",
                schema: "messaging",
                table: "scheduled_messages",
                column: "scheduled_at",
                filter: "status = 'Scheduled'");

            migrationBuilder.CreateIndex(
                name: "UX_scheduled_messages_active_conversation",
                schema: "messaging",
                table: "scheduled_messages",
                column: "conversation_id",
                unique: true,
                filter: "status IN ('Scheduled','Sending','Failed')");

            migrationBuilder.CreateIndex(
                name: "UX_scheduled_messages_conversation_client",
                schema: "messaging",
                table: "scheduled_messages",
                columns: new[] { "conversation_id", "client_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scheduled_messages",
                schema: "messaging");

            migrationBuilder.DropColumn(
                name: "retry_disabled",
                schema: "messaging",
                table: "messages");
        }
    }
}
