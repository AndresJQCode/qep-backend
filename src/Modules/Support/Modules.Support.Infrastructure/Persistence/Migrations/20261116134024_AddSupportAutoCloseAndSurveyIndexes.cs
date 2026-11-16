using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportAutoCloseAndSurveyIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_tickets_resolved_auto_close",
                schema: "support",
                table: "tickets",
                columns: new[] { "resolved_at", "id" },
                filter: "status = 'Resolved'");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_surveys_awaiting_comment",
                schema: "support",
                table: "ticket_surveys",
                columns: new[] { "answered_at", "id" },
                filter: "answered_via = 'WhatsApp' AND rating IS NOT NULL AND comment IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_surveys_awaiting_rating",
                schema: "support",
                table: "ticket_surveys",
                columns: new[] { "sent_at", "id" },
                filter: "status = 'Sent' AND rating IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_surveys_comment_message",
                schema: "support",
                table: "ticket_surveys",
                column: "comment_message_id",
                filter: "comment_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_surveys_rating_message",
                schema: "support",
                table: "ticket_surveys",
                column: "rating_message_id",
                filter: "rating_message_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tickets_resolved_auto_close",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_ticket_surveys_awaiting_comment",
                schema: "support",
                table: "ticket_surveys");

            migrationBuilder.DropIndex(
                name: "IX_ticket_surveys_awaiting_rating",
                schema: "support",
                table: "ticket_surveys");

            migrationBuilder.DropIndex(
                name: "IX_ticket_surveys_comment_message",
                schema: "support",
                table: "ticket_surveys");

            migrationBuilder.DropIndex(
                name: "IX_ticket_surveys_rating_message",
                schema: "support",
                table: "ticket_surveys");
        }
    }
}
