using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modules.Automations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRunUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "response_bytes",
                schema: "automations",
                table: "http_calls",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "step_number",
                schema: "automations",
                table: "http_calls",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "run_units",
                schema: "automations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_step = table.Column<int>(type: "integer", nullable: false),
                    trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    inbound_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    steps = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'"),
                    closed_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    closed_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_units", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_run_units_run_inbound",
                schema: "automations",
                table: "run_units",
                columns: new[] { "run_id", "inbound_message_id" },
                unique: true,
                filter: "inbound_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_run_units_started_at",
                schema: "automations",
                table: "run_units",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "IX_run_units_tenant_run",
                schema: "automations",
                table: "run_units",
                columns: new[] { "tenant_id", "run_id", "base_step" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run_units",
                schema: "automations");

            migrationBuilder.DropColumn(
                name: "response_bytes",
                schema: "automations",
                table: "http_calls");

            migrationBuilder.DropColumn(
                name: "step_number",
                schema: "automations",
                table: "http_calls");
        }
    }
}
