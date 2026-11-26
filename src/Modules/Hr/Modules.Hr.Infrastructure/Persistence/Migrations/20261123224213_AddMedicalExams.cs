using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Hr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMedicalExams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "exam_frequency_months",
                schema: "hr",
                table: "positions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "medical_exams",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    exam_date = table.Column<DateOnly>(type: "date", nullable: false),
                    provider = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    restrictions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    recommendations = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    restrictions_until = table.Column<DateOnly>(type: "date", nullable: true),
                    document_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medical_exams", x => x.id);
                    table.CheckConstraint("CK_medical_exams_type", "type IN ('PreEmployment','Periodic','OccupationChange','PostIncapacity','ReturnToWork','FollowUp','Exit')");
                    table.ForeignKey(
                        name: "FK_medical_exams_employees_employee_id",
                        column: x => x.employee_id,
                        principalSchema: "hr",
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "exit_exam_requirements",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    terminated_on = table.Column<DateOnly>(type: "date", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    exam_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notified_on = table.Column<DateOnly>(type: "date", nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exit_exam_requirements", x => x.id);
                    table.CheckConstraint("CK_exit_exam_requirements_status", "status IN ('Pending','Done','NotAttended')");
                    table.ForeignKey(
                        name: "FK_exit_exam_requirements_employees_employee_id",
                        column: x => x.employee_id,
                        principalSchema: "hr",
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_exit_exam_requirements_employment_contracts_contract_id",
                        column: x => x.contract_id,
                        principalSchema: "hr",
                        principalTable: "employment_contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_exit_exam_requirements_medical_exams_exam_id",
                        column: x => x.exam_id,
                        principalSchema: "hr",
                        principalTable: "medical_exams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_positions_exam_frequency_months",
                schema: "hr",
                table: "positions",
                sql: "exam_frequency_months IS NULL OR exam_frequency_months BETWEEN 1 AND 36");

            migrationBuilder.CreateIndex(
                name: "IX_exit_exam_requirements_contract_id",
                schema: "hr",
                table: "exit_exam_requirements",
                column: "contract_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_exit_exam_requirements_employee_id",
                schema: "hr",
                table: "exit_exam_requirements",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_exit_exam_requirements_exam_id",
                schema: "hr",
                table: "exit_exam_requirements",
                column: "exam_id");

            migrationBuilder.CreateIndex(
                name: "IX_exit_exam_requirements_tenant_employee",
                schema: "hr",
                table: "exit_exam_requirements",
                columns: new[] { "tenant_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_exit_exam_requirements_tenant_status",
                schema: "hr",
                table: "exit_exam_requirements",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_medical_exams_employee_exam_date",
                schema: "hr",
                table: "medical_exams",
                columns: new[] { "employee_id", "exam_date" });

            migrationBuilder.CreateIndex(
                name: "IX_medical_exams_tenant_employee_type_date",
                schema: "hr",
                table: "medical_exams",
                columns: new[] { "tenant_id", "employee_id", "type", "exam_date" },
                unique: true);

            // Spec 2026-11-23 §5.3 (D-7): the contracts already terminated and still inside their exit exam deadline. The deadline
            // is counted Monday to Friday only (the Colombian holidays live in Messaging, out of reach of Hr), so a row may get a due
            // date a day or two earlier than the exact one; see MedicalExamBackfill.
            migrationBuilder.Sql(MedicalExamBackfill.Sql("CURRENT_DATE"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "exit_exam_requirements",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "medical_exams",
                schema: "hr");

            migrationBuilder.DropCheckConstraint(
                name: "CK_positions_exam_frequency_months",
                schema: "hr",
                table: "positions");

            migrationBuilder.DropColumn(
                name: "exam_frequency_months",
                schema: "hr",
                table: "positions");
        }
    }
}
