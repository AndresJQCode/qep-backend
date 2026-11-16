using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketExtension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "extended_at",
                schema: "support",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "extension_business_days",
                schema: "support",
                table: "tickets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "extension_reason",
                schema: "support",
                table: "tickets",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_tickets_extension",
                schema: "support",
                table: "tickets",
                sql: "(extended_at IS NULL) = (extension_reason IS NULL) AND extension_business_days >= 0 AND (extended_at IS NOT NULL OR extension_business_days = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events",
                sql: "kind IN ('Created','StatusChanged','AssigneeChanged','TypeChanged','CustomerChanged','MessagePinned','MessageReleased','MessageClassified','MessageDismissed','FollowThreadChanged','ReplySent','ConversationLinked','Extended')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_tickets_extension",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events");

            migrationBuilder.DropColumn(
                name: "extended_at",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "extension_business_days",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "extension_reason",
                schema: "support",
                table: "tickets");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events",
                sql: "kind IN ('Created','StatusChanged','AssigneeChanged','TypeChanged','CustomerChanged','MessagePinned','MessageReleased','MessageClassified','MessageDismissed','FollowThreadChanged','ReplySent','ConversationLinked')");
        }
    }
}
