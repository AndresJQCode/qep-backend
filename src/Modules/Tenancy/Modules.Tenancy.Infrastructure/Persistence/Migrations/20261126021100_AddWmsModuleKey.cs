using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWmsModuleKey : Migration
    {
        /// <summary>Spec WMS 2026-10-31 §1.1: <c>wms</c> queda fuera de <c>DefaultForNewTenants</c> y sin backfill: se prende por la
        /// consola del operador. Se regeneró al traer develop a la rama para que sea la última que reescribe el <c>CHECK</c>: en una base
        /// nueva, las migraciones de módulos posteriores a 2026-11-02 dejarían la lista sin <c>wms</c>.</summary>
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
                sql: "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging','pipeline','shopify','automations','suppliers','billing','developer','loans','store','ai','copilot','support','purchases','wholesale','siigo','hr','payroll','scheduling','wms')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver atrás descarta las filas de wms: Postgres valida las filas existentes al agregar el CHECK anterior.
            migrationBuilder.Sql("DELETE FROM tenancy.tenant_modules WHERE module_key = 'wms';");

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
    }
}
