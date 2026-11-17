using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportTicketPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events");

            migrationBuilder.AddColumn<string>(
                name: "priority",
                schema: "support",
                table: "tickets",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Medium");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tickets_priority",
                schema: "support",
                table: "tickets",
                sql: "priority IN ('Low','Medium','High','Urgent')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events",
                sql: "kind IN ('Created','StatusChanged','AssigneeChanged','TypeChanged','CustomerChanged','MessagePinned','MessageReleased','MessageClassified','MessageDismissed','FollowThreadChanged','ReplySent','ConversationLinked','Extended','PriorityChanged')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_tickets_priority",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events");

            migrationBuilder.DropColumn(
                name: "priority",
                schema: "support",
                table: "tickets");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events",
                sql: "kind IN ('Created','StatusChanged','AssigneeChanged','TypeChanged','CustomerChanged','MessagePinned','MessageReleased','MessageClassified','MessageDismissed','FollowThreadChanged','ReplySent','ConversationLinked','Extended')");
        }
    }
}
