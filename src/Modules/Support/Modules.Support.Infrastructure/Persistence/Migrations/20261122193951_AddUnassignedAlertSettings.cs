using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUnassignedAlertSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "unassigned_alert_enabled",
                schema: "support",
                table: "tenant_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "unassigned_alert_enabled_at",
                schema: "support",
                table: "tenant_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "unassigned_alert_hours",
                schema: "support",
                table: "tenant_settings",
                type: "integer",
                nullable: false,
                defaultValue: 4);

            migrationBuilder.AddColumn<Guid[]>(
                name: "unassigned_alert_recipient_ids",
                schema: "support",
                table: "tenant_settings",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<Guid>(
                name: "unassigned_alert_schedule_id",
                schema: "support",
                table: "tenant_settings",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "unassigned_alert_enabled",
                schema: "support",
                table: "tenant_settings");

            migrationBuilder.DropColumn(
                name: "unassigned_alert_enabled_at",
                schema: "support",
                table: "tenant_settings");

            migrationBuilder.DropColumn(
                name: "unassigned_alert_hours",
                schema: "support",
                table: "tenant_settings");

            migrationBuilder.DropColumn(
                name: "unassigned_alert_recipient_ids",
                schema: "support",
                table: "tenant_settings");

            migrationBuilder.DropColumn(
                name: "unassigned_alert_schedule_id",
                schema: "support",
                table: "tenant_settings");
        }
    }
}
