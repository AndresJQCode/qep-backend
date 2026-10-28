using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Shopify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCityCorrectionJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "city_correction_jobs",
                schema: "shopify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    synonym_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mirror_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    operator_subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    skip_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    requested_city_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_city_correction_jobs", x => x.id);
                    table.CheckConstraint("CK_city_correction_jobs_kind", "kind IN ('Resolve','Recorrect')");
                    table.CheckConstraint("CK_city_correction_jobs_status", "status IN ('Pending','Applied','Skipped','Failed')");
                    table.CheckConstraint("CK_city_correction_jobs_target", "target IN ('Order','Customer')");
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "shopify",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claimed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_messages", x => new { x.consumer, x.message_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_city_correction_jobs_message_target",
                schema: "shopify",
                table: "city_correction_jobs",
                columns: new[] { "source_message_id", "target", "target_id" },
                unique: true,
                filter: "source_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_city_correction_jobs_status_lease",
                schema: "shopify",
                table: "city_correction_jobs",
                columns: new[] { "status", "lease_until" });

            migrationBuilder.CreateIndex(
                name: "IX_city_correction_jobs_synonym_status",
                schema: "shopify",
                table: "city_correction_jobs",
                columns: new[] { "synonym_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "city_correction_jobs",
                schema: "shopify");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "shopify");
        }
    }
}
