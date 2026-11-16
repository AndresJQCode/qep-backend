using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportPinReissuedEventKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events",
                sql: "kind IN ('Created','StatusChanged','AssigneeChanged','TypeChanged','CustomerChanged','MessagePinned','MessageReleased','MessageClassified','MessageDismissed','FollowThreadChanged','ReplySent','ConversationLinked','Extended','PriorityChanged','PinReissued')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events",
                sql: "kind IN ('Created','StatusChanged','AssigneeChanged','TypeChanged','CustomerChanged','MessagePinned','MessageReleased','MessageClassified','MessageDismissed','FollowThreadChanged','ReplySent','ConversationLinked','Extended','PriorityChanged')");
        }
    }
}
