using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectQuotations : Migration
    {
        /// <summary>Spec 2026-11-26 (cotizaciones de proyecto) §5. Aditiva (expand): <c>kind</c> tiene default
        /// <c>Standard</c> en la base y las demás columnas son nulas, así que un pod viejo y los <c>INSERT</c> crudos siguen
        /// escribiendo. El módulo <c>projects</c> no se prende hasta que no quede un pod viejo.</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "quotations",
                table: "quotations",
                type: "text",
                nullable: false,
                defaultValue: "Standard");

            migrationBuilder.AddColumn<string>(
                name: "project_pricing_mode",
                schema: "quotations",
                table: "quotations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "project_scope",
                schema: "quotations",
                table: "quotations",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "quotations",
                table: "orders",
                type: "text",
                nullable: false,
                defaultValue: "Standard");

            migrationBuilder.AddColumn<string>(
                name: "project_scope",
                schema: "quotations",
                table: "orders",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_quotations_kind",
                schema: "quotations",
                table: "quotations",
                sql: "kind IN ('Standard','Project')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_quotations_project_pricing_mode",
                schema: "quotations",
                table: "quotations",
                sql: "project_pricing_mode IN ('Global','PerPhase')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_orders_kind",
                schema: "quotations",
                table: "orders",
                sql: "kind IN ('Standard','Project')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_quotations_kind",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_quotations_project_pricing_mode",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_orders_kind",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "project_pricing_mode",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "project_scope",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "project_scope",
                schema: "quotations",
                table: "orders");
        }
    }
}
