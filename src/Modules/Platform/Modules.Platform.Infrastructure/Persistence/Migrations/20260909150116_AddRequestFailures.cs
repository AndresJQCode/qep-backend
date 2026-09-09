using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.CreateTable(
                name: "request_failures",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    module = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: false),
                    error_code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    detail = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    trace_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_failures", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_request_failures_tenant_error_code",
                schema: "platform",
                table: "request_failures",
                columns: new[] { "tenant_id", "error_code" });

            migrationBuilder.CreateIndex(
                name: "IX_request_failures_tenant_module",
                schema: "platform",
                table: "request_failures",
                columns: new[] { "tenant_id", "module" });

            migrationBuilder.CreateIndex(
                name: "IX_request_failures_tenant_occurred_at",
                schema: "platform",
                table: "request_failures",
                columns: new[] { "tenant_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "request_failures",
                schema: "platform");
        }
    }
}
