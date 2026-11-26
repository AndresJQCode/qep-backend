using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderTemplateKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_message_templates_kind",
                schema: "messaging",
                table: "message_templates");

            migrationBuilder.CreateIndex(
                name: "UX_message_templates_order",
                schema: "messaging",
                table: "message_templates",
                columns: new[] { "tenant_id", "connection_id" },
                unique: true,
                filter: "kind = 'Order'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_message_templates_kind",
                schema: "messaging",
                table: "message_templates",
                sql: "kind IN ('Free','Quotation','External','BillingReminder','BillingOverdue','Order')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver atrás descarta la plantilla de pedido: Postgres valida las filas existentes al agregar el CHECK anterior
            // (mismo criterio que AddBillingTemplateKinds).
            migrationBuilder.Sql("DELETE FROM messaging.message_templates WHERE kind = 'Order';");

            migrationBuilder.DropIndex(
                name: "UX_message_templates_order",
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
                sql: "kind IN ('Free','Quotation','External','BillingReminder','BillingOverdue')");
        }
    }
}
