using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportMessaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events");

            migrationBuilder.AddColumn<string>(
                name: "conversation_channel",
                schema: "support",
                table: "tickets",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "conversation_id",
                schema: "support",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "follow_off_at",
                schema: "support",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "follow_since",
                schema: "support",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "follow_thread",
                schema: "support",
                table: "tickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ticket_messages",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_messages", x => x.id);
                    table.CheckConstraint("CK_ticket_messages_state", "state IN ('Linked','Pinned','Released')");
                    table.ForeignKey(
                        name: "FK_ticket_messages_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "support",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_replies",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    delivery = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    text = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_replies", x => x.id);
                    table.CheckConstraint("CK_ticket_replies_channel", "channel IN ('Thread','WhatsAppTemplate','Email')");
                    table.CheckConstraint("CK_ticket_replies_delivery", "delivery IN ('Text','Template','Email')");
                    table.CheckConstraint("CK_ticket_replies_status", "status IN ('Pending','Sent','Failed')");
                    table.ForeignKey(
                        name: "FK_ticket_replies_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "support",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "unclassified_messages",
                schema: "support",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolution = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_ticket_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_unclassified_messages", x => x.message_id);
                    table.CheckConstraint("CK_unclassified_messages_resolution", "resolution IN ('Assigned','Dismissed','Reassigned')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_tenant_conversation",
                schema: "support",
                table: "tickets",
                columns: new[] { "tenant_id", "conversation_id" },
                filter: "conversation_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tickets_conversation_channel",
                schema: "support",
                table: "tickets",
                sql: "conversation_channel IN ('WhatsApp','Instagram')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tickets_follow",
                schema: "support",
                table: "tickets",
                sql: "NOT follow_thread OR (conversation_id IS NOT NULL AND follow_since IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events",
                sql: "kind IN ('Created','StatusChanged','AssigneeChanged','TypeChanged','CustomerChanged','MessagePinned','MessageReleased','MessageClassified','MessageDismissed','FollowThreadChanged','ReplySent','ConversationLinked')");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_messages_conversation",
                schema: "support",
                table: "ticket_messages",
                columns: new[] { "tenant_id", "conversation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_messages_ticket_message",
                schema: "support",
                table: "ticket_messages",
                columns: new[] { "ticket_id", "message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_messages_ticket_visible",
                schema: "support",
                table: "ticket_messages",
                columns: new[] { "ticket_id", "occurred_at", "message_id" },
                descending: new[] { false, true, true },
                filter: "state <> 'Released'");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_replies_author",
                schema: "support",
                table: "ticket_replies",
                column: "author_member_id");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_replies_ticket_client",
                schema: "support",
                table: "ticket_replies",
                columns: new[] { "ticket_id", "client_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_unclassified_pending",
                schema: "support",
                table: "unclassified_messages",
                columns: new[] { "tenant_id", "conversation_id" },
                filter: "resolution IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_unclassified_resolved_by",
                schema: "support",
                table: "unclassified_messages",
                column: "resolved_by_member_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_messages",
                schema: "support");

            migrationBuilder.DropTable(
                name: "ticket_replies",
                schema: "support");

            migrationBuilder.DropTable(
                name: "unclassified_messages",
                schema: "support");

            migrationBuilder.DropIndex(
                name: "IX_tickets_tenant_conversation",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tickets_conversation_channel",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tickets_follow",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events");

            migrationBuilder.DropColumn(
                name: "conversation_channel",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "conversation_id",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "follow_off_at",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "follow_since",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "follow_thread",
                schema: "support",
                table: "tickets");

            // Revisión final (M5): la restricción vieja no admite las clases de la fase 2; sin borrar esas filas, el Down falla al
            // volver a crearla en una base que ya las tiene.
            migrationBuilder.Sql(
                "DELETE FROM support.ticket_events WHERE kind IN ('MessagePinned','MessageReleased','MessageClassified','MessageDismissed','FollowThreadChanged','ReplySent','ConversationLinked');");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events",
                sql: "kind IN ('Created','StatusChanged','AssigneeChanged','TypeChanged','CustomerChanged')");
        }
    }
}
