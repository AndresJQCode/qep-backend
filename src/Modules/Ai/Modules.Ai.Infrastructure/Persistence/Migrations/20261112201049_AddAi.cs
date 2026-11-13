using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Ai.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ai");

            migrationBuilder.CreateTable(
                name: "agents",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Draft"),
                    draft_config = table.Column<string>(type: "jsonb", nullable: false),
                    draft_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    draft_updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    published_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.id);
                    table.CheckConstraint("CK_agents_published", "(status = 'Draft') = (published_version_id IS NULL)");
                    table.CheckConstraint("CK_agents_status", "status IN ('Draft','Active','Paused')");
                });

            migrationBuilder.CreateTable(
                name: "agent_connections",
                schema: "ai",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_connections", x => new { x.agent_id, x.connection_id });
                    table.ForeignKey(
                        name: "FK_agent_connections_agents_agent_id",
                        column: x => x.agent_id,
                        principalSchema: "ai",
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_versions",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    provider = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_agent_versions_agents_agent_id",
                        column: x => x.agent_id,
                        principalSchema: "ai",
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_connections_tenant_connection",
                schema: "ai",
                table: "agent_connections",
                columns: new[] { "tenant_id", "connection_id" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_versions_agent_number",
                schema: "ai",
                table: "agent_versions",
                columns: new[] { "agent_id", "number" },
                unique: true);

            // D-A11: único por tenant sin importar mayúsculas. EF no modela índices por expresión (como IX_chatbots_tenant_name).
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_agents_tenant_name\" ON ai.agents (tenant_id, lower(name));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ai.\"IX_agents_tenant_name\";");

            migrationBuilder.DropTable(
                name: "agent_connections",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "agent_versions",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "agents",
                schema: "ai");
        }
    }
}
