using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modules.Ai.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiModelRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_jobs",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    test_id = table.Column<Guid>(type: "uuid", nullable: true),
                    up_to_seq = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_jobs", x => x.id);
                    table.CheckConstraint("CK_model_jobs_owner", "(session_id IS NULL) <> (test_id IS NULL)");
                    table.CheckConstraint("CK_model_jobs_status", "status IN ('Pending','Running','Done','Discarded','Failed')");
                });

            migrationBuilder.CreateTable(
                name: "tests",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    transcript = table.Column<string>(type: "jsonb", nullable: false),
                    pending_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    handed_off = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tool_calls",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    call_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tool = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    arguments = table.Column<string>(type: "jsonb", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_calls", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "usage_records",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    test_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    served_model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    cache_creation_input_tokens = table.Column<int>(type: "integer", nullable: true),
                    cache_read_input_tokens = table.Column<int>(type: "integer", nullable: true),
                    outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    called_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_records", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_jobs_due",
                schema: "ai",
                table: "model_jobs",
                column: "next_attempt_at",
                filter: "status IN ('Pending','Running')");

            migrationBuilder.CreateIndex(
                name: "IX_model_jobs_session_open",
                schema: "ai",
                table: "model_jobs",
                column: "session_id",
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_model_jobs_test",
                schema: "ai",
                table: "model_jobs",
                column: "test_id",
                filter: "test_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_tests_tenant_agent",
                schema: "ai",
                table: "tests",
                columns: new[] { "tenant_id", "agent_id" });

            migrationBuilder.CreateIndex(
                name: "IX_tool_calls_created",
                schema: "ai",
                table: "tool_calls",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_tool_calls_job_call",
                schema: "ai",
                table: "tool_calls",
                columns: new[] { "job_id", "call_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_usage_records_tenant_called",
                schema: "ai",
                table: "usage_records",
                columns: new[] { "tenant_id", "called_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_usage_records_test",
                schema: "ai",
                table: "usage_records",
                column: "test_id",
                filter: "test_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_jobs",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "tests",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "tool_calls",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "usage_records",
                schema: "ai");
        }
    }
}
