using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSchedulingAfterPayrollModuleKey : Migration
    {
        /// <summary>Merge de la Agenda con develop (plan 2026-11-21, entrada 51): <c>AddSchedulingModuleKey</c> (2026-11-21) es más
        /// vieja que <c>AddPayrollModuleKey</c> (2026-11-22) y las dos reescriben el mismo <c>CHECK</c>. En una base nueva corre primero
        /// la de la Agenda y la de nómina deja la lista sin <c>scheduling</c>; ésta, la última, deja la lista completa sin importar el
        /// orden en que hayan corrido las otras dos.</summary>
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
