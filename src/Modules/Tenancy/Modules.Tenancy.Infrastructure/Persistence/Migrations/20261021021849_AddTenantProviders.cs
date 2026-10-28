using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_changes_kind",
                schema: "tenancy",
                table: "tenant_changes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_changes_module_key",
                schema: "tenancy",
                table: "tenant_changes");

            migrationBuilder.AddColumn<string>(
                name: "provider_key",
                schema: "tenancy",
                table: "tenant_changes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tenant_providers",
                schema: "tenancy",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    enabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_providers", x => new { x.tenant_id, x.provider_key });
                    table.CheckConstraint("CK_tenant_providers_provider_key", "provider_key IN ('zenvia')");
                    table.CheckConstraint("CK_tenant_providers_status", "status IN ('active','inactive')");
                    table.ForeignKey(
                        name: "FK_tenant_providers_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_changes_kind",
                schema: "tenancy",
                table: "tenant_changes",
                sql: "kind IN ('module','tenant_status','provider')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_changes_module_key",
                schema: "tenancy",
                table: "tenant_changes",
                sql: "(kind = 'module') = (module_key IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_changes_provider_key",
                schema: "tenancy",
                table: "tenant_changes",
                sql: "(kind = 'provider') = (provider_key IS NOT NULL) AND (provider_key IS NULL OR provider_key IN ('zenvia'))");

            // D14: los tenants que ya usan Zenvia lo conservan. Una fila activa por tenant con al menos una
            // conexión de Zenvia, en cualquier estado. Sin fila en tenant_changes: es la migración, no una
            // decisión del operador. El guard es porque en una base nueva Integrations puede migrar después
            // que Tenancy; ahí no hay conexiones que respetar. El JOIN deja afuera conexiones de tenants que
            // no existen (la FK lo rechazaría), y con ON CONFLICT el mismo SQL se puede correr a mano para
            // reparar un tenant sin chocar con la PK.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('integrations.connections') IS NOT NULL THEN
                        INSERT INTO tenancy.tenant_providers (tenant_id, provider_key, status, enabled_at, status_changed_at)
                        SELECT DISTINCT c.tenant_id, 'zenvia', 'active', now(), now()
                        FROM integrations.connections c
                        JOIN tenancy.tenants t ON t.id = c.tenant_id
                        WHERE c.provider_key = 'zenvia'
                        ON CONFLICT DO NOTHING;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Antes que los CHECK de abajo: los de antes rechazan toda fila de proveedor.
            migrationBuilder.Sql("DELETE FROM tenancy.tenant_changes WHERE kind = 'provider';");

            migrationBuilder.DropTable(
                name: "tenant_providers",
                schema: "tenancy");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_changes_kind",
                schema: "tenancy",
                table: "tenant_changes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_changes_module_key",
                schema: "tenancy",
                table: "tenant_changes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_changes_provider_key",
                schema: "tenancy",
                table: "tenant_changes");

            migrationBuilder.DropColumn(
                name: "provider_key",
                schema: "tenancy",
                table: "tenant_changes");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_changes_kind",
                schema: "tenancy",
                table: "tenant_changes",
                sql: "kind IN ('module','tenant_status')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_changes_module_key",
                schema: "tenancy",
                table: "tenant_changes",
                sql: "(kind = 'module' AND module_key IS NOT NULL) OR (kind = 'tenant_status' AND module_key IS NULL)");
        }
    }
}
