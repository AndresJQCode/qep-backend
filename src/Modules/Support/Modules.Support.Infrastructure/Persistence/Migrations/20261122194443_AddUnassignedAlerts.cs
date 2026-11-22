using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUnassignedAlerts : Migration
    {
        /// <summary>
        /// Spec 2026-11-22 D-15: the tickets that are, when this runs, without an assignee in a status that counts start their clock at the time
        /// of the migration (no avalanche of alerts for tickets that were already waiting); every other ticket stays null. Public so a test can run
        /// exactly this statement.
        /// </summary>
        public const string BackfillSql =
            "UPDATE support.tickets SET unassigned_since = now() "
            + "WHERE assignee_member_id IS NULL AND status IN ('New', 'Open', 'WaitingInternal')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "unassigned_since",
                schema: "support",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(BackfillSql);

            migrationBuilder.CreateTable(
                name: "unassigned_alerts",
                schema: "support",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unassigned_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_count = table.Column<int>(type: "integer", nullable: false),
                    alerted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_unassigned_alerts", x => new { x.ticket_id, x.unassigned_since });
                });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_unassigned",
                schema: "support",
                table: "tickets",
                column: "tenant_id",
                filter: "assignee_member_id IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "unassigned_alerts",
                schema: "support");

            migrationBuilder.DropIndex(
                name: "IX_tickets_unassigned",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "unassigned_since",
                schema: "support",
                table: "tickets");
        }
    }
}
