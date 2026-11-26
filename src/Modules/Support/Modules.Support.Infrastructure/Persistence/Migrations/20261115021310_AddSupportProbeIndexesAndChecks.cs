using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportProbeIndexesAndChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_tickets_assignee",
                schema: "support",
                table: "tickets",
                column: "assignee_member_id");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tickets_manual_medium",
                schema: "support",
                table: "tickets",
                sql: "manual_medium IN ('Phone','InPerson','Email','Letter','Other')");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_events_actor",
                schema: "support",
                table: "ticket_events",
                column: "actor_member_id");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events",
                sql: "kind IN ('Created','StatusChanged','AssigneeChanged','TypeChanged','CustomerChanged')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tickets_assignee",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tickets_manual_medium",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_ticket_events_actor",
                schema: "support",
                table: "ticket_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events");
        }
    }
}
