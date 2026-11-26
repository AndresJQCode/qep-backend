using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Notifications.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInAppNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "in_app_notifications",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_in_app_notifications", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_in_app_notifications_created",
                schema: "notifications",
                table: "in_app_notifications",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_in_app_notifications_member_created",
                schema: "notifications",
                table: "in_app_notifications",
                columns: new[] { "tenant_id", "member_id", "created_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_in_app_notifications_member_dedupe",
                schema: "notifications",
                table: "in_app_notifications",
                columns: new[] { "tenant_id", "member_id", "dedupe_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_in_app_notifications_member_unread",
                schema: "notifications",
                table: "in_app_notifications",
                columns: new[] { "tenant_id", "member_id" },
                filter: "read_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_in_app_notifications_resource",
                schema: "notifications",
                table: "in_app_notifications",
                columns: new[] { "tenant_id", "kind", "resource_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "in_app_notifications",
                schema: "notifications");
        }
    }
}
