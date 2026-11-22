using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Payroll.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurringConcepts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_period_events_period_occurred",
                schema: "payroll",
                table: "period_events");

            migrationBuilder.AddColumn<int>(
                name: "sequence",
                schema: "payroll",
                table: "period_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // The history of a period keeps the order the events were written in: rows that already exist are numbered by
            // (occurred_at, id) per period, the best order that was available when they were written.
            migrationBuilder.Sql(
                """
                UPDATE payroll.period_events AS evt
                SET sequence = ordered.position
                FROM (
                    SELECT id, row_number() OVER (PARTITION BY period_id ORDER BY occurred_at, id) AS position
                    FROM payroll.period_events
                ) AS ordered
                WHERE evt.id = ordered.id;
                """);

            migrationBuilder.CreateTable(
                name: "recurring_concepts",
                schema: "payroll",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    concept_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    salary_constitutive = table.Column<bool>(type: "boolean", nullable: true),
                    cap = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recurring_concepts", x => x.id);
                    table.CheckConstraint("CK_recurring_concepts_mode", "mode IN ('Fixed','Proportional')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_period_events_period_occurred",
                schema: "payroll",
                table: "period_events",
                columns: new[] { "period_id", "occurred_at", "sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_recurring_concepts_tenant_company_employee",
                schema: "payroll",
                table: "recurring_concepts",
                columns: new[] { "tenant_id", "company_id", "employee_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recurring_concepts",
                schema: "payroll");

            migrationBuilder.DropIndex(
                name: "IX_period_events_period_occurred",
                schema: "payroll",
                table: "period_events");

            migrationBuilder.DropColumn(
                name: "sequence",
                schema: "payroll",
                table: "period_events");

            migrationBuilder.CreateIndex(
                name: "IX_period_events_period_occurred",
                schema: "payroll",
                table: "period_events",
                columns: new[] { "period_id", "occurred_at" });
        }
    }
}
