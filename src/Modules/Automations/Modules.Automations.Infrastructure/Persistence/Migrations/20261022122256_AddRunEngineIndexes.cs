using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Automations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRunEngineIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_conversation_runs_tenant_conversation_started",
                schema: "automations",
                table: "conversation_runs",
                columns: new[] { "tenant_id", "conversation_id", "started_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_runs_waiting_updated",
                schema: "automations",
                table: "conversation_runs",
                column: "updated_at",
                filter: "status = 'Waiting'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_conversation_runs_tenant_conversation_started",
                schema: "automations",
                table: "conversation_runs");

            migrationBuilder.DropIndex(
                name: "IX_conversation_runs_waiting_updated",
                schema: "automations",
                table: "conversation_runs");
        }
    }
}
