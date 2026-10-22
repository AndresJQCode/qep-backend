using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modules.Automations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "automations");

            migrationBuilder.CreateTable(
                name: "chatbot_templates",
                schema: "automations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    category = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, defaultValue: ""),
                    flow = table.Column<string>(type: "jsonb", nullable: false),
                    layout = table.Column<string>(type: "jsonb", nullable: false),
                    parameters = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chatbot_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "chatbots",
                schema: "automations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, defaultValue: ""),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Draft"),
                    trigger = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, defaultValue: "NewConversation"),
                    keywords = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'"),
                    restart_keyword = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "reiniciar"),
                    max_attempts = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)3),
                    draft_flow = table.Column<string>(type: "jsonb", nullable: false),
                    draft_layout = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'"),
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
                    table.PrimaryKey("PK_chatbots", x => x.id);
                    table.CheckConstraint("CK_chatbots_max_attempts", "max_attempts BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_chatbots_published", "(status = 'Draft') = (published_version_id IS NULL)");
                    table.CheckConstraint("CK_chatbots_status", "status IN ('Draft','Active','Paused')");
                    table.CheckConstraint("CK_chatbots_trigger", "trigger IN ('NewConversation','Keyword','OutsideHours')");
                });

            migrationBuilder.CreateTable(
                name: "conversation_runs",
                schema: "automations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chatbot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chatbot_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    current_node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    step_number = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    attempts = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    variables = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'"),
                    trace = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'"),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "http_calls",
                schema: "automations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    simulation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    called_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_http_calls", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "simulations",
                schema: "automations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chatbot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    live_http = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    state = table.Column<string>(type: "jsonb", nullable: false),
                    transcript = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_simulations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "chatbot_connections",
                schema: "automations",
                columns: table => new
                {
                    chatbot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chatbot_connections", x => new { x.chatbot_id, x.connection_id });
                    table.ForeignKey(
                        name: "FK_chatbot_connections_chatbots_chatbot_id",
                        column: x => x.chatbot_id,
                        principalSchema: "automations",
                        principalTable: "chatbots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chatbot_versions",
                schema: "automations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    chatbot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    flow = table.Column<string>(type: "jsonb", nullable: false),
                    layout = table.Column<string>(type: "jsonb", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chatbot_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_chatbot_versions_chatbots_chatbot_id",
                        column: x => x.chatbot_id,
                        principalSchema: "automations",
                        principalTable: "chatbots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chatbot_connections_active",
                schema: "automations",
                table: "chatbot_connections",
                columns: new[] { "tenant_id", "connection_id" },
                unique: true,
                filter: "active");

            migrationBuilder.CreateIndex(
                name: "IX_chatbot_versions_chatbot_number",
                schema: "automations",
                table: "chatbot_versions",
                columns: new[] { "chatbot_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_conversation_runs_live",
                schema: "automations",
                table: "conversation_runs",
                column: "conversation_id",
                unique: true,
                filter: "status IN ('Running','Waiting')");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_runs_tenant_chatbot_started",
                schema: "automations",
                table: "conversation_runs",
                columns: new[] { "tenant_id", "chatbot_id", "started_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_http_calls_tenant_called",
                schema: "automations",
                table: "http_calls",
                columns: new[] { "tenant_id", "called_at" },
                descending: new[] { false, true });

            // Spec 2026-10-21 §4.2: EF no modela índices por expresión (mismo caso que AddBusinessHours de Messaging). La unidad de
            // trabajo traduce los dos por nombre a 409 name_taken.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_chatbots_tenant_name\" ON automations.chatbots (tenant_id, lower(name));");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_chatbot_templates_tenant_name\" ON automations.chatbot_templates (tenant_id, lower(name));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS automations.\"IX_chatbot_templates_tenant_name\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS automations.\"IX_chatbots_tenant_name\";");

            migrationBuilder.DropTable(
                name: "chatbot_connections",
                schema: "automations");

            migrationBuilder.DropTable(
                name: "chatbot_templates",
                schema: "automations");

            migrationBuilder.DropTable(
                name: "chatbot_versions",
                schema: "automations");

            migrationBuilder.DropTable(
                name: "conversation_runs",
                schema: "automations");

            migrationBuilder.DropTable(
                name: "http_calls",
                schema: "automations");

            migrationBuilder.DropTable(
                name: "simulations",
                schema: "automations");

            migrationBuilder.DropTable(
                name: "chatbots",
                schema: "automations");
        }
    }
}
