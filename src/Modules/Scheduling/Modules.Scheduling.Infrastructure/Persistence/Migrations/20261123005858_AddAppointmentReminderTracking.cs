using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Scheduling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentReminderTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_appointments_reminder_due",
                schema: "scheduling",
                table: "appointments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_appointment_events_action",
                schema: "scheduling",
                table: "appointment_events");

            migrationBuilder.AddColumn<int>(
                name: "reminder_attempts",
                schema: "scheduling",
                table: "appointments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reminder_claimed_until",
                schema: "scheduling",
                table: "appointments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reminder_last_error",
                schema: "scheduling",
                table: "appointments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reminder_skipped_reason",
                schema: "scheduling",
                table: "appointments",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_appointments_reminder_due",
                schema: "scheduling",
                table: "appointments",
                column: "starts_at",
                filter: "status = 'Confirmed' AND reminder_sent_at IS NULL AND reminder_skipped_reason IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_appointment_events_action",
                schema: "scheduling",
                table: "appointment_events",
                sql: "action IN ('Created','Confirmed','Cancelled','Attended','NoShow','Rescheduled','ReminderSent','ReminderSkipped')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver atrás descarta las entradas «Recordatorio omitido» del historial: Postgres valida las filas existentes al agregar el CHECK
            // anterior.
            migrationBuilder.Sql("DELETE FROM scheduling.appointment_events WHERE action = 'ReminderSkipped';");

            migrationBuilder.DropIndex(
                name: "IX_appointments_reminder_due",
                schema: "scheduling",
                table: "appointments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_appointment_events_action",
                schema: "scheduling",
                table: "appointment_events");

            migrationBuilder.DropColumn(
                name: "reminder_attempts",
                schema: "scheduling",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "reminder_claimed_until",
                schema: "scheduling",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "reminder_last_error",
                schema: "scheduling",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "reminder_skipped_reason",
                schema: "scheduling",
                table: "appointments");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_reminder_due",
                schema: "scheduling",
                table: "appointments",
                column: "starts_at",
                filter: "status = 'Confirmed' AND reminder_sent_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_appointment_events_action",
                schema: "scheduling",
                table: "appointment_events",
                sql: "action IN ('Created','Confirmed','Cancelled','Attended','NoShow','Rescheduled','ReminderSent')");
        }
    }
}
