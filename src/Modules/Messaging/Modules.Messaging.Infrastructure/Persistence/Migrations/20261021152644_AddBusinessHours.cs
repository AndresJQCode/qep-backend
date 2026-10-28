using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "business_hours_schedules",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    weekly = table.Column<string>(type: "jsonb", nullable: false),
                    exceptions = table.Column<string>(type: "jsonb", nullable: false),
                    holiday_country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_hours_schedules", x => x.id);
                    table.CheckConstraint("CK_business_hours_holiday_country", "holiday_country IS NULL OR holiday_country IN ('CO')");
                });

            migrationBuilder.CreateTable(
                name: "connection_business_hours",
                schema: "messaging",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connection_business_hours", x => x.connection_id);
                    table.ForeignKey(
                        name: "FK_connection_business_hours_schedule",
                        column: x => x.schedule_id,
                        principalSchema: "messaging",
                        principalTable: "business_hours_schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_connection_business_hours_schedule",
                schema: "messaging",
                table: "connection_business_hours",
                column: "schedule_id");

            // Spec 2026-10-21 §5.1: EF no modela índices por expresión ni parciales con nombre propio (mismo caso que AddResolutionReasons).
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_business_hours_tenant_name\" ON messaging.business_hours_schedules (tenant_id, lower(name));");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_business_hours_tenant_default\" ON messaging.business_hours_schedules (tenant_id) WHERE is_default;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS messaging.\"IX_business_hours_tenant_default\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS messaging.\"IX_business_hours_tenant_name\";");

            migrationBuilder.DropTable(
                name: "connection_business_hours",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "business_hours_schedules",
                schema: "messaging");
        }
    }
}
