using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportFormalAndSurveyTemplateKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_message_templates_kind",
                schema: "messaging",
                table: "message_templates");

            migrationBuilder.CreateIndex(
                name: "UX_message_templates_support_formal_reply",
                schema: "messaging",
                table: "message_templates",
                columns: new[] { "tenant_id", "connection_id" },
                unique: true,
                filter: "kind = 'SupportFormalReply'");

            migrationBuilder.CreateIndex(
                name: "UX_message_templates_support_survey",
                schema: "messaging",
                table: "message_templates",
                columns: new[] { "tenant_id", "connection_id" },
                unique: true,
                filter: "kind = 'SupportSurvey'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_message_templates_kind",
                schema: "messaging",
                table: "message_templates",
                sql: "kind IN ('Free','Quotation','External','BillingReminder','BillingOverdue','Order','SupportReply','SupportFormalReply','SupportSurvey')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver atrás descarta las plantillas de Soporte de la fase 4b: Postgres valida las filas existentes al agregar el CHECK
            // anterior (mismo criterio que AddSupportReplyTemplateKind).
            migrationBuilder.Sql("DELETE FROM messaging.message_templates WHERE kind IN ('SupportFormalReply','SupportSurvey');");

            migrationBuilder.DropIndex(
                name: "UX_message_templates_support_formal_reply",
                schema: "messaging",
                table: "message_templates");

            migrationBuilder.DropIndex(
                name: "UX_message_templates_support_survey",
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
                sql: "kind IN ('Free','Quotation','External','BillingReminder','BillingOverdue','Order','SupportReply')");
        }
    }
}
