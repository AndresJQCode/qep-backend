using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskNoticeTemplateKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_message_templates_kind",
                schema: "messaging",
                table: "message_templates");

            migrationBuilder.CreateIndex(
                name: "UX_message_templates_task_notice",
                schema: "messaging",
                table: "message_templates",
                columns: new[] { "tenant_id", "connection_id" },
                unique: true,
                filter: "kind = 'TaskNotice'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_message_templates_kind",
                schema: "messaging",
                table: "message_templates",
                sql: "kind IN ('Free','Quotation','External','BillingReminder','BillingOverdue','Order','SupportReply','SupportFormalReply','SupportSurvey','WholesaleInvitationNotice','Invoice','AppointmentReminder','TaskNotice')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rolling back drops the task notice templates: Postgres validates existing rows when it re-adds the previous CHECK
            // (same criterion as AddInvoiceTemplateKind).
            migrationBuilder.Sql("DELETE FROM messaging.message_templates WHERE kind = 'TaskNotice';");

            migrationBuilder.DropIndex(
                name: "UX_message_templates_task_notice",
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
                sql: "kind IN ('Free','Quotation','External','BillingReminder','BillingOverdue','Order','SupportReply','SupportFormalReply','SupportSurvey','WholesaleInvitationNotice','Invoice','AppointmentReminder')");
        }
    }
}
