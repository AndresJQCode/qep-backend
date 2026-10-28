using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelineModuleKey : Migration
    {
        /// <summary>Spec 2026-10-13 D3: otro módulo = otra migración que cambia el CHECK, a propósito
        /// (<c>TenantModuleKey.cs</c>). <c>pipeline</c> no entra en <c>DefaultForNewTenants</c>: se prende por tenant.</summary>
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
                sql: "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging','pipeline')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver atrás descarta las filas de pipeline: Postgres valida las filas existentes al agregar el CHECK de ocho claves.
            migrationBuilder.Sql("DELETE FROM tenancy.tenant_modules WHERE module_key = 'pipeline';");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules",
                sql: "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging')");
        }
    }
}
