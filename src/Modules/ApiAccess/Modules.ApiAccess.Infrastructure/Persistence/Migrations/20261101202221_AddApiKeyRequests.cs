using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.ApiAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_key_requests",
                schema: "api_access",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    api_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    api_key_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: false),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    trace_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_key_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_key_requests_occurred",
                schema: "api_access",
                table: "api_key_requests",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_api_key_requests_tenant_occurred",
                schema: "api_access",
                table: "api_key_requests",
                columns: new[] { "tenant_id", "occurred_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_key_requests",
                schema: "api_access");
        }
    }
}
