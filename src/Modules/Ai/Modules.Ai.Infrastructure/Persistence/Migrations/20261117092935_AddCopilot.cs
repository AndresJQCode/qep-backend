using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Ai.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCopilot : Migration
    {
        /// <summary>Spec 2026-11-17 (copiloto) §5.1: configuración, hilos y mensajes; <c>model_jobs</c> suma el dueño <c>copilot_thread_id</c>
        /// (exactamente uno de tres) y <c>usage_records</c> el hilo y el miembro, con <c>agent_id</c> nulo en el consumo del copiloto. Sin
        /// backfill: las filas existentes son todas de agentes. (<c>model_jobs</c> no tiene <c>agent_id</c>: el CHECK de agente va sólo en
        /// <c>usage_records</c>.)</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_model_jobs_owner",
                schema: "ai",
                table: "model_jobs");

            migrationBuilder.AlterColumn<Guid>(
                name: "agent_id",
                schema: "ai",
                table: "usage_records",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "copilot_thread_id",
                schema: "ai",
                table: "usage_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "member_user_id",
                schema: "ai",
                table: "usage_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "copilot_thread_id",
                schema: "ai",
                table: "model_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "copilot_settings",
                schema: "ai",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    provider = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    instructions = table.Column<string>(type: "text", nullable: false),
                    monthly_token_budget = table.Column<long>(type: "bigint", nullable: true),
                    retention_days = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_copilot_settings", x => x.tenant_id);
                    table.CheckConstraint("CK_copilot_settings_enabled", "NOT enabled OR (provider IS NOT NULL AND model IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "copilot_threads",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_copilot_threads", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "copilot_messages",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    thread_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    citations = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    progress = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_copilot_messages", x => x.id);
                    table.CheckConstraint("CK_copilot_messages_role", "role IN ('User','Assistant')");
                    table.CheckConstraint("CK_copilot_messages_status", "status IN ('Done','Pending','Failed')");
                    table.ForeignKey(
                        name: "FK_copilot_messages_copilot_threads_thread_id",
                        column: x => x.thread_id,
                        principalSchema: "ai",
                        principalTable: "copilot_threads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_usage_records_copilot",
                schema: "ai",
                table: "usage_records",
                columns: new[] { "tenant_id", "member_user_id", "called_at" },
                descending: new[] { false, false, true },
                filter: "copilot_thread_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_usage_records_copilot_member",
                schema: "ai",
                table: "usage_records",
                sql: "copilot_thread_id IS NULL OR member_user_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_usage_records_owner",
                schema: "ai",
                table: "usage_records",
                sql: "(copilot_thread_id IS NULL) = (agent_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_model_jobs_copilot_thread",
                schema: "ai",
                table: "model_jobs",
                column: "copilot_thread_id",
                filter: "copilot_thread_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_model_jobs_owner",
                schema: "ai",
                table: "model_jobs",
                sql: "((session_id IS NOT NULL)::int + (test_id IS NOT NULL)::int + (copilot_thread_id IS NOT NULL)::int) = 1");

            migrationBuilder.CreateIndex(
                name: "IX_copilot_messages_pending",
                schema: "ai",
                table: "copilot_messages",
                column: "thread_id",
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_copilot_messages_thread_seq",
                schema: "ai",
                table: "copilot_messages",
                columns: new[] { "thread_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_copilot_threads_activity",
                schema: "ai",
                table: "copilot_threads",
                column: "last_activity_at");

            migrationBuilder.CreateIndex(
                name: "IX_copilot_threads_owner",
                schema: "ai",
                table: "copilot_threads",
                columns: new[] { "tenant_id", "user_id", "last_activity_at" },
                descending: new[] { false, false, true });
        }

        /// <summary>Borra las tres tablas (con los hilos y su texto) y vuelve <c>agent_id NOT NULL</c> y el CHECK de dos dueños. <b>Falla con
        /// trabajos o consumos del copiloto</b> (filas con <c>agent_id</c> nulo o sin sesión ni prueba): borrarlas a mano antes pierde el consumo
        /// pagado. No se corre en producción.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "copilot_messages",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "copilot_settings",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "copilot_threads",
                schema: "ai");

            migrationBuilder.DropIndex(
                name: "IX_usage_records_copilot",
                schema: "ai",
                table: "usage_records");

            migrationBuilder.DropCheckConstraint(
                name: "CK_usage_records_copilot_member",
                schema: "ai",
                table: "usage_records");

            migrationBuilder.DropCheckConstraint(
                name: "CK_usage_records_owner",
                schema: "ai",
                table: "usage_records");

            migrationBuilder.DropIndex(
                name: "IX_model_jobs_copilot_thread",
                schema: "ai",
                table: "model_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_model_jobs_owner",
                schema: "ai",
                table: "model_jobs");

            migrationBuilder.DropColumn(
                name: "copilot_thread_id",
                schema: "ai",
                table: "usage_records");

            migrationBuilder.DropColumn(
                name: "member_user_id",
                schema: "ai",
                table: "usage_records");

            migrationBuilder.DropColumn(
                name: "copilot_thread_id",
                schema: "ai",
                table: "model_jobs");

            migrationBuilder.AlterColumn<Guid>(
                name: "agent_id",
                schema: "ai",
                table: "usage_records",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_model_jobs_owner",
                schema: "ai",
                table: "model_jobs",
                sql: "(session_id IS NULL) <> (test_id IS NULL)");
        }
    }
}
