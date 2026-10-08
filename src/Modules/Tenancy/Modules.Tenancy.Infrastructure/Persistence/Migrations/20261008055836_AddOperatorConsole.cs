using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOperatorConsole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "status",
                schema: "tenancy",
                table: "tenant_modules",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValueSql: "'active'");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "status_changed_at",
                schema: "tenancy",
                table: "tenant_modules",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            // Spec 2026-10-08 §7: las filas existentes quedan activas (el DEFAULT ya las llenó) desde que
            // se crearon. Antes de los CHECK, en la misma transacción.
            migrationBuilder.Sql("UPDATE tenancy.tenant_modules SET status_changed_at = enabled_at;");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_modules_source",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_modules_source",
                schema: "tenancy",
                table: "tenant_modules",
                sql: "source IN ('backfill','signup','seed','manual','operator')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_modules_status",
                schema: "tenancy",
                table: "tenant_modules",
                sql: "status IN ('active','inactive')");

            migrationBuilder.CreateTable(
                name: "tenant_changes",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    module_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    from_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    to_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_changes", x => x.id);
                    table.CheckConstraint("CK_tenant_changes_kind", "kind IN ('module','tenant_status')");
                    table.CheckConstraint("CK_tenant_changes_module_key", "(kind = 'module' AND module_key IS NOT NULL) OR (kind = 'tenant_status' AND module_key IS NULL)");
                    table.CheckConstraint("CK_tenant_changes_reason", "reason IN ('contract','courtesy','nonpayment','cancellation','correction')");
                    table.ForeignKey(
                        name: "FK_tenant_changes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_changes_tenant_id_occurred_at",
                schema: "tenancy",
                table: "tenant_changes",
                columns: new[] { "tenant_id", "occurred_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Spec 2026-10-08 §7: en el modelo anterior, inactivo = sin fila; y el CHECK viejo de source
            // no acepta 'operator'. OJO: este Down le devuelve el acceso a los tenants Suspended, porque
            // el código anterior ignora el estado del tenant.
            migrationBuilder.Sql("DELETE FROM tenancy.tenant_modules WHERE status = 'inactive';");
            migrationBuilder.Sql("UPDATE tenancy.tenant_modules SET source = 'manual' WHERE source = 'operator';");

            migrationBuilder.DropTable(
                name: "tenant_changes",
                schema: "tenancy");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_modules_source",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_modules_status",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.DropColumn(
                name: "status",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.DropColumn(
                name: "status_changed_at",
                schema: "tenancy",
                table: "tenant_modules");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_modules_source",
                schema: "tenancy",
                table: "tenant_modules",
                sql: "source IN ('backfill','signup','seed','manual')");
        }
    }
}
