using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Scheduling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentEventByChannelAndReminderSender : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "reminder_sender_member_id",
                schema: "scheduling",
                table: "settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "by_channel",
                schema: "scheduling",
                table: "appointment_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reminder_sender_member_id",
                schema: "scheduling",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "by_channel",
                schema: "scheduling",
                table: "appointment_events");
        }
    }
}
