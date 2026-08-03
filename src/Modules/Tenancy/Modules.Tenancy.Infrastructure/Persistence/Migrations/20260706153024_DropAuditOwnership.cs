using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropAuditOwnership : Migration
    {
        private static readonly string[] EntriesTenantIdOccurredAtColumns = { "tenant_id", "occurred_at" };

        // Tenancy relinquishes ownership of audit.entries (ADR 0019). The table becomes an
        // ExcludeFromMigrations write projection in TenancyDbContext; the Audit module's
        // InitialAudit migration is its sole owner and recreates it (as a superset). The
        // audit schema is left in place for the Audit migration's EnsureSchema.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entries",
                schema: "audit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "entries",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    resource_id = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    changed_fields = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_entries_tenant_id_occurred_at",
                schema: "audit",
                table: "entries",
                columns: EntriesTenantIdOccurredAtColumns);
        }
    }
}
