using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Siigo.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSiigoCallFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "call_failures",
                schema: "siigo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: true),
                    reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    siigo_invoice_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    request_curl = table.Column<string>(type: "text", nullable: false),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    trace_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_call_failures", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_call_failures_occurred",
                schema: "siigo",
                table: "call_failures",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_call_failures_tenant_occurred",
                schema: "siigo",
                table: "call_failures",
                columns: new[] { "tenant_id", "occurred_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "call_failures",
                schema: "siigo");
        }
    }
}
