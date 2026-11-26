using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modules.Ai.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "result",
                schema: "ai",
                table: "model_jobs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sessions",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    next_seq = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    replies_without_human = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    superseded_in_row = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_inbound_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    end_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.id);
                    table.CheckConstraint("CK_sessions_status", "status IN ('Live','HandedOff','WindowClosed','Interrupted','Failed','Abandoned')");
                });

            migrationBuilder.CreateTable(
                name: "turns",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sources = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    truncated = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_turns", x => x.id);
                    table.CheckConstraint("CK_turns_role", "role IN ('Customer','Agent','Greeting')");
                    table.ForeignKey(
                        name: "FK_turns_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "ai",
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_conversation_started",
                schema: "ai",
                table: "sessions",
                columns: new[] { "conversation_id", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_live",
                schema: "ai",
                table: "sessions",
                column: "conversation_id",
                unique: true,
                filter: "status = 'Live'");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_live_updated",
                schema: "ai",
                table: "sessions",
                column: "updated_at",
                filter: "status = 'Live'");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_tenant_agent_started",
                schema: "ai",
                table: "sessions",
                columns: new[] { "tenant_id", "agent_id", "started_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_turns_session_seq",
                schema: "ai",
                table: "turns",
                columns: new[] { "session_id", "seq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "turns",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "sessions",
                schema: "ai");

            migrationBuilder.DropColumn(
                name: "result",
                schema: "ai",
                table: "model_jobs");
        }
    }
}
