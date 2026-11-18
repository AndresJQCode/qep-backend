using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWholesaleModuleKey : Migration
    {
        /// <summary>Spec 2026-11-17 (mayorista): <c>wholesale</c> queda fuera de <c>DefaultForNewTenants</c> y sin backfill (se
        /// prende por la consola del operador), y el origen <c>invitation</c> marca las filas que crea la aceptación de una
        /// invitación de mayorista. Sólo se ensanchan los dos <c>CHECK</c>.</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_modules_source",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules",
                sql: "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging','pipeline','shopify','automations','suppliers','billing','developer','loans','store','ai','copilot','support','purchases','wholesale')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_modules_source",
                schema: "tenancy",
                table: "tenant_modules",
                sql: "source IN ('backfill','signup','seed','manual','operator','invitation')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Postgres valida las filas existentes al agregar los CHECK anteriores: volver atrás descarta las filas de
            // wholesale y deja las de una invitación como si las hubiera creado el signup.
            migrationBuilder.Sql("DELETE FROM tenancy.tenant_modules WHERE module_key = 'wholesale';");
            migrationBuilder.Sql("UPDATE tenancy.tenant_modules SET source = 'signup' WHERE source = 'invitation';");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_modules_source",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules",
                sql: "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging','pipeline','shopify','automations','suppliers','billing','developer','loans','store','ai','copilot','support','purchases')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_modules_source",
                schema: "tenancy",
                table: "tenant_modules",
                sql: "source IN ('backfill','signup','seed','manual','operator')");
        }
    }
}
