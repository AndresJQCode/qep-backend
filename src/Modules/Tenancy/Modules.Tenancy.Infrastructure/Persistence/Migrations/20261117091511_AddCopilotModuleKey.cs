using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCopilotModuleKey : Migration
    {
        /// <summary>Spec 2026-11-17 (copiloto), D-C2 y OC-4: <c>copilot</c> queda fuera de <c>DefaultForNewTenants</c> y sin
        /// backfill: se prende por la consola del operador.</summary>
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
                sql: "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging','pipeline','shopify','automations','suppliers','billing','developer','loans','store','ai','copilot','support')");
        }

        /// <summary>Vuelve el CHECK sin <c>copilot</c>. A propósito no borra filas: si algún tenant ya tiene la clave, Postgres
        /// valida las filas existentes y el <c>Down</c> falla. No se corre en producción con el módulo encendido en algún tenant.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules",
                sql: "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging','pipeline','shopify','automations','suppliers','billing','developer','loans','store','ai','support')");
        }
    }
}
