using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessagingModuleKey : Migration
    {
        /// <summary>Spec 2026-10-09 §6.2: otro módulo = otra migración que cambia el CHECK, a propósito
        /// (<c>TenantModuleKey.cs</c>). <c>messaging</c> no entra en <c>DefaultForNewTenants</c>: se prende por tenant.</summary>
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
                sql: "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver atrás descarta las filas de messaging: Postgres valida las filas existentes al
            // agregar el CHECK de siete claves, y con una sola fila de messaging (la semilla siembra
            // una por tenant) el rollback moriría con 23514 sin explicar nada. El historial de
            // contratación de messaging se pierde: es el precio de deshacer el módulo.
            migrationBuilder.Sql("DELETE FROM tenancy.tenant_modules WHERE module_key = 'messaging';");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_modules_module_key",
                schema: "tenancy",
                table: "tenant_modules",
                sql: "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos')");
        }
    }
}
