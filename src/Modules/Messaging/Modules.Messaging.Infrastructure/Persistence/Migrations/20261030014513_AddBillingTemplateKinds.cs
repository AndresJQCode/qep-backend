using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingTemplateKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_message_templates_kind",
                schema: "messaging",
                table: "message_templates");

            migrationBuilder.CreateIndex(
                name: "UX_message_templates_billing",
                schema: "messaging",
                table: "message_templates",
                columns: new[] { "tenant_id", "connection_id", "kind" },
                unique: true,
                filter: "kind IN ('BillingReminder','BillingOverdue')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_message_templates_kind",
                schema: "messaging",
                table: "message_templates",
                sql: "kind IN ('Free','Quotation','External','BillingReminder','BillingOverdue')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver atrás descarta las plantillas de cobros: Postgres valida las filas existentes al agregar el CHECK anterior
            // (mismo criterio que AddBillingModuleKey de Tenancy).
            migrationBuilder.Sql("DELETE FROM messaging.message_templates WHERE kind IN ('BillingReminder','BillingOverdue');");

            migrationBuilder.DropIndex(
                name: "UX_message_templates_billing",
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
                sql: "kind IN ('Free','Quotation','External')");
        }
    }
}
