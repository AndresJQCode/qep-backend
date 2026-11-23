using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSchedulingModuleKey : Migration
    {
        /// <summary>Spec 2026-11-21 (Agenda): <c>scheduling</c> queda fuera de <c>DefaultForNewTenants</c> y sin backfill (se prende
        /// por tenant). Sólo se ensancha el <c>CHECK</c>.
        /// Merge con develop (plan 2026-11-21, entrada 51): esta migración es más vieja que <c>AddPayrollModuleKey</c> y una base que ya
        /// corrió esa la aplica después, así que el <c>CHECK</c> incluye <c>payroll</c> para no rechazar sus filas. Si corre antes,
        /// <c>AddPayrollModuleKey</c> deja la lista sin <c>scheduling</c> y <c>AddSchedulingAfterPayrollModuleKey</c> la repone.</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules",
                sql: "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging','pipeline','shopify','automations','suppliers','billing','developer','loans','store','ai','copilot','support','purchases','wholesale','siigo','hr','payroll','scheduling')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Postgres valida las filas existentes al agregar el CHECK anterior: volver atrás descarta las filas de scheduling.
            migrationBuilder.Sql("DELETE FROM tenancy.tenant_modules WHERE module_key = 'scheduling';");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules",
                sql: "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging','pipeline','shopify','automations','suppliers','billing','developer','loans','store','ai','copilot','support','purchases','wholesale','siigo','hr','payroll')");
        }
    }
}
