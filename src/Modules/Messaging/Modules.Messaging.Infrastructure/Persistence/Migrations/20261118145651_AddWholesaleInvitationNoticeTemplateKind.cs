using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWholesaleInvitationNoticeTemplateKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_message_templates_kind",
                schema: "messaging",
                table: "message_templates");

            migrationBuilder.CreateIndex(
                name: "UX_message_templates_wholesale_invitation_notice",
                schema: "messaging",
                table: "message_templates",
                columns: new[] { "tenant_id", "connection_id" },
                unique: true,
                filter: "kind = 'WholesaleInvitationNotice'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_message_templates_kind",
                schema: "messaging",
                table: "message_templates",
                sql: "kind IN ('Free','Quotation','External','BillingReminder','BillingOverdue','Order','SupportReply','SupportFormalReply','SupportSurvey','WholesaleInvitationNotice')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver atrás descarta las plantillas del aviso de invitación: Postgres valida las filas existentes al agregar el CHECK
            // anterior (mismo criterio que AddSupportFormalAndSurveyTemplateKinds).
            migrationBuilder.Sql("DELETE FROM messaging.message_templates WHERE kind = 'WholesaleInvitationNotice';");

            migrationBuilder.DropIndex(
                name: "UX_message_templates_wholesale_invitation_notice",
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
                sql: "kind IN ('Free','Quotation','External','BillingReminder','BillingOverdue','Order','SupportReply','SupportFormalReply','SupportSurvey')");
        }
    }
}
