using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectsModuleKey : Migration
    {
        /// <summary>Spec 2026-11-26 (cotizaciones de proyecto): <c>projects</c> queda fuera de <c>DefaultForNewTenants</c> y sin
        /// backfill: se prende por la consola del operador, cuando no quede un pod viejo (<c>TenantModuleKey.Parse</c> lanza con una
        /// clave que no conoce).</summary>
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
                sql: "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging','pipeline','shopify','automations','suppliers','billing','developer','loans','store','ai','copilot','support','purchases','wholesale','siigo','hr','payroll','scheduling','wms','projects')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver atrás descarta las filas de projects: Postgres valida las filas existentes al agregar el CHECK anterior.
            migrationBuilder.Sql("DELETE FROM tenancy.tenant_modules WHERE module_key = 'projects';");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules",
                sql: "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging','pipeline','shopify','automations','suppliers','billing','developer','loans','store','ai','copilot','support','purchases','wholesale','siigo','hr','payroll','scheduling','wms')");
        }
    }
}
