using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExportJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "export_jobs",
                schema: "quotations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    filters = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    row_count = table.Column<int>(type: "integer", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_export_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sales_tenant_converted_at_number",
                schema: "quotations",
                table: "sales",
                columns: new[] { "tenant_id", "converted_at", "sale_number" });

            migrationBuilder.CreateIndex(
                name: "IX_quotations_tenant_created_at_number",
                schema: "quotations",
                table: "quotations",
                columns: new[] { "tenant_id", "created_at", "quotation_number" });

            migrationBuilder.CreateIndex(
                name: "IX_export_jobs_claim",
                schema: "quotations",
                table: "export_jobs",
                columns: new[] { "status", "next_attempt_at" },
                filter: "status IN ('Pending', 'Processing')");

            migrationBuilder.CreateIndex(
                name: "IX_export_jobs_requester",
                schema: "quotations",
                table: "export_jobs",
                columns: new[] { "tenant_id", "requested_by", "status" },
                filter: "status IN ('Pending', 'Processing')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "export_jobs",
                schema: "quotations");

            migrationBuilder.DropIndex(
                name: "IX_sales_tenant_converted_at_number",
                schema: "quotations",
                table: "sales");

            migrationBuilder.DropIndex(
                name: "IX_quotations_tenant_created_at_number",
                schema: "quotations",
                table: "quotations");
        }
    }
}
