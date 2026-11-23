using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Notifications.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskNoticeEscalations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "task_notice_escalations",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    in_app_notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_by_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    skip_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    channel_attempts = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    claimed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_notice_escalations", x => x.id);
                    table.ForeignKey(
                        name: "FK_task_notice_escalations_in_app_notifications_in_app_notific~",
                        column: x => x.in_app_notification_id,
                        principalSchema: "notifications",
                        principalTable: "in_app_notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_task_notice_escalations_due",
                schema: "notifications",
                table: "task_notice_escalations",
                columns: new[] { "status", "due_at" });

            migrationBuilder.CreateIndex(
                name: "UX_task_notice_escalations_notification",
                schema: "notifications",
                table: "task_notice_escalations",
                column: "in_app_notification_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_notice_escalations",
                schema: "notifications");
        }
    }
}
