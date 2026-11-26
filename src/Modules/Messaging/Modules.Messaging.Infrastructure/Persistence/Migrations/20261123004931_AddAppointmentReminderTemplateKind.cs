using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentReminderTemplateKind : Migration
    {
        /// <summary>Merge con develop (plan 2026-11-21, entrada 51): <c>AddInvoiceTemplateKind</c> es más vieja y reescribe el mismo
        /// <c>CHECK</c>, así que esta lista incluye <c>Invoice</c>; sin eso, aplicarla sobre una base con facturas fallaría.</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_message_templates_kind",
                schema: "messaging",
                table: "message_templates");

            migrationBuilder.CreateIndex(
                name: "UX_message_templates_appointment_reminder",
                schema: "messaging",
                table: "message_templates",
                columns: new[] { "tenant_id", "connection_id" },
                unique: true,
                filter: "kind = 'AppointmentReminder'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_message_templates_kind",
                schema: "messaging",
                table: "message_templates",
                sql: "kind IN ('Free','Quotation','External','BillingReminder','BillingOverdue','Order','SupportReply','SupportFormalReply','SupportSurvey','WholesaleInvitationNotice','Invoice','AppointmentReminder')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver atrás descarta las plantillas del recordatorio de cita: Postgres valida las filas existentes al agregar el CHECK
            // anterior (mismo criterio que AddWholesaleInvitationNoticeTemplateKind).
            migrationBuilder.Sql("DELETE FROM messaging.message_templates WHERE kind = 'AppointmentReminder';");

            migrationBuilder.DropIndex(
                name: "UX_message_templates_appointment_reminder",
                schema: "messaging",
                table: "message_templates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_message_templates_kind",
                schema: "messaging",
                table: "message_templates");

            migrationBuilder.AddCheckConstraint(
                name: "CK_message_templates_kind",
                schema: "messaging",
                table: "message_templates",
                sql: "kind IN ('Free','Quotation','External','BillingReminder','BillingOverdue','Order','SupportReply','SupportFormalReply','SupportSurvey','WholesaleInvitationNotice','Invoice')");
        }
    }
}
