using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantModules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_modules",
                schema: "tenancy",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    enabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_modules", x => new { x.tenant_id, x.module_key });
                    table.CheckConstraint("CK_tenant_modules_module_key", "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos')");
                    table.CheckConstraint("CK_tenant_modules_source", "source IN ('backfill','signup','seed','manual')");
                    table.ForeignKey(
                        name: "FK_tenant_modules_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Spec 2026-10-07: los tenants que existen al migrar conservan los seis módulos de hoy
            // (sin pos), en la misma migración y por lo tanto en la misma transacción que crea la
            // tabla, igual que el backfill de AddTenantOwnerMembership. Un tenant que un pod viejo
            // cree durante el rolling update queda sin filas: se repara con el SQL de «Operación».
            migrationBuilder.Sql(
                """
                INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
                SELECT t.id, m.key, now(), 'backfill'
                FROM tenancy.tenants t
                CROSS JOIN (VALUES ('catalog'),('customers'),('companies'),('quotations'),('orders'),('reporting')) AS m(key);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_modules",
                schema: "tenancy");
        }
    }
}
