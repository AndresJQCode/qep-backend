using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHrModuleKey : Migration
    {
        /// <summary>Spec 2026-11-20 (RRHH fase 1): <c>hr</c> queda fuera de <c>DefaultForNewTenants</c> y sin backfill (se prende
        /// por tenant). Sólo se ensancha el <c>CHECK</c>.</summary>
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
                sql: "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging','pipeline','shopify','automations','suppliers','billing','developer','loans','store','ai','copilot','support','purchases','wholesale','siigo','hr')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Postgres valida las filas existentes al agregar el CHECK anterior: volver atrás descarta las filas de hr.
            migrationBuilder.Sql("DELETE FROM tenancy.tenant_modules WHERE module_key = 'hr';");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules",
                sql: "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging','pipeline','shopify','automations','suppliers','billing','developer','loans','store','ai','copilot','support','purchases','wholesale','siigo')");
        }
    }
}
