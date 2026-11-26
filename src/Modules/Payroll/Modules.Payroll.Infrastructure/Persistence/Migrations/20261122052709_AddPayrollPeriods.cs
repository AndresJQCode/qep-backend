using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Payroll.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPayrollPeriods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "periods",
                schema: "payroll",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    half = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    outdated = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    legal_values = table.Column<string>(type: "jsonb", nullable: true),
                    legal_values_date = table.Column<DateOnly>(type: "date", nullable: true),
                    engine_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    excluded = table.Column<string>(type: "jsonb", nullable: true),
                    last_liquidated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    last_liquidated_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    last_liquidated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_periods", x => x.id);
                    table.CheckConstraint("CK_periods_half", "half IN ('First','Second')");
                    table.CheckConstraint("CK_periods_status", "status IN ('Draft','Liquidated','Approved','Closed','Reopened')");
                });

            migrationBuilder.CreateTable(
                name: "employee_results",
                schema: "payroll",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    document_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    full_name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    position = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    workplace = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    worked_days = table.Column<int>(type: "integer", nullable: false),
                    negative_net = table.Column<bool>(type: "boolean", nullable: false),
                    final_settlement_pending = table.Column<bool>(type: "boolean", nullable: false),
                    engine_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: false),
                    summary = table.Column<string>(type: "jsonb", nullable: false),
                    month = table.Column<string>(type: "jsonb", nullable: false),
                    recurring_applied = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_results", x => x.id);
                    table.ForeignKey(
                        name: "FK_employee_results_periods_period_id",
                        column: x => x.period_id,
                        principalSchema: "payroll",
                        principalTable: "periods",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "period_events",
                schema: "payroll",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    from_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    to_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    absences_confirmed = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_period_events", x => x.id);
                    table.CheckConstraint("CK_period_events_action", "action IN ('Created','InputsImported','Liquidated','Approved','Closed','Reopened','MarkedOutdated')");
                    table.CheckConstraint("CK_period_events_from_status", "from_status IS NULL OR from_status IN ('Draft','Liquidated','Approved','Closed','Reopened')");
                    table.CheckConstraint("CK_period_events_to_status", "to_status IS NULL OR to_status IN ('Draft','Liquidated','Approved','Closed','Reopened')");
                    table.ForeignKey(
                        name: "FK_period_events_periods_period_id",
                        column: x => x.period_id,
                        principalSchema: "payroll",
                        principalTable: "periods",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "period_inputs",
                schema: "payroll",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    concept_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    hours = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    salary_constitutive = table.Column<bool>(type: "boolean", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_period_inputs", x => x.id);
                    table.CheckConstraint("CK_period_inputs_kind", "kind IN ('Overtime','Income','Deduction')");
                    table.ForeignKey(
                        name: "FK_period_inputs_periods_period_id",
                        column: x => x.period_id,
                        principalSchema: "payroll",
                        principalTable: "periods",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_employee_results_period_employee",
                schema: "payroll",
                table: "employee_results",
                columns: new[] { "period_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_period_events_period_occurred",
                schema: "payroll",
                table: "period_events",
                columns: new[] { "period_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_period_inputs_period_employee",
                schema: "payroll",
                table: "period_inputs",
                columns: new[] { "period_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_periods_tenant_company_start",
                schema: "payroll",
                table: "periods",
                columns: new[] { "tenant_id", "company_id", "start_date" },
                unique: true);

            // FK real a la empresa (R-19): una empresa con períodos no se borra; CompaniesUnitOfWork traduce el RESTRICT a 422
            // companies.company.in_use. A mano porque EF no modela relaciones entre DbContext de módulos distintos (mismo
            // precedente que pos, hr y company_settings).
            migrationBuilder.AddForeignKey(
                name: "FK_periods_companies_company_id",
                schema: "payroll",
                table: "periods",
                column: "company_id",
                principalSchema: "companies",
                principalTable: "companies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_periods_companies_company_id",
                schema: "payroll",
                table: "periods");

            migrationBuilder.DropTable(
                name: "employee_results",
                schema: "payroll");

            migrationBuilder.DropTable(
                name: "period_events",
                schema: "payroll");

            migrationBuilder.DropTable(
                name: "period_inputs",
                schema: "payroll");

            migrationBuilder.DropTable(
                name: "periods",
                schema: "payroll");
        }
    }
}
