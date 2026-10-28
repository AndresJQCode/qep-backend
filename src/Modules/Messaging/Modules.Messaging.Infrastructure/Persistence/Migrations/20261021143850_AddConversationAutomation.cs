using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "automation_pause_reason",
                schema: "messaging",
                table: "conversations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "automation_paused_at",
                schema: "messaging",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "automation_paused_by_member_id",
                schema: "messaging",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "automation_started_at",
                schema: "messaging",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "automation_status",
                schema: "messaging",
                table: "conversations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_tenant_automation_activity",
                schema: "messaging",
                table: "conversations",
                columns: new[] { "tenant_id", "automation_status", "last_activity_at" },
                descending: new[] { false, false, true },
                filter: "automation_status <> 'None'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_automation_assignment",
                schema: "messaging",
                table: "conversations",
                sql: "automation_status <> 'Active' OR assigned_member_id IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_automation_pause",
                schema: "messaging",
                table: "conversations",
                sql: "(automation_status = 'Paused') = (automation_paused_at IS NOT NULL AND automation_pause_reason IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_automation_reason",
                schema: "messaging",
                table: "conversations",
                sql: "automation_pause_reason IS NULL OR automation_pause_reason IN ('AdvisorTook','AdvisorReplied','AdvisorTransferred','Manual','Handoff')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_automation_resolved",
                schema: "messaging",
                table: "conversations",
                sql: "status <> 'Resolved' OR automation_status = 'None'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_automation_started",
                schema: "messaging",
                table: "conversations",
                sql: "(automation_status = 'None') = (automation_started_at IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_automation_status",
                schema: "messaging",
                table: "conversations",
                sql: "automation_status IN ('None','Active','Paused')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_conversations_tenant_automation_activity",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_conversations_automation_assignment",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_conversations_automation_pause",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_conversations_automation_reason",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_conversations_automation_resolved",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_conversations_automation_started",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_conversations_automation_status",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "automation_pause_reason",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "automation_paused_at",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "automation_paused_by_member_id",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "automation_started_at",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "automation_status",
                schema: "messaging",
                table: "conversations");
        }
    }
}
