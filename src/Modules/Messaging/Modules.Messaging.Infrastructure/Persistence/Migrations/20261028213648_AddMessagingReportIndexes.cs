using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessagingReportIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_messages_events_reopened",
                schema: "messaging",
                table: "messages",
                columns: new[] { "tenant_id", "created_at" },
                filter: "direction = 3 AND kind = 13");

            migrationBuilder.CreateIndex(
                name: "IX_messages_tenant_created",
                schema: "messaging",
                table: "messages",
                columns: new[] { "tenant_id", "created_at" })
                .Annotation("Npgsql:IndexInclude", new[] { "conversation_id", "direction" });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_tenant_created",
                schema: "messaging",
                table: "conversations",
                columns: new[] { "tenant_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_events_reopened",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_messages_tenant_created",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_conversations_tenant_created",
                schema: "messaging",
                table: "conversations");
        }
    }
}
