using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Payroll.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPayroll : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "payroll");

            migrationBuilder.CreateTable(
                name: "company_settings",
                schema: "payroll",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transport_frequency = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    transport_cap_base = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    overtime_base = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    exonerated = table.Column<bool>(type: "boolean", nullable: false),
                    dual_control = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_settings", x => x.id);
                    table.CheckConstraint("CK_company_settings_overtime_base", "overtime_base IN ('SalaryOnly','SalaryAndMonthSalaryIncome')");
                    table.CheckConstraint("CK_company_settings_transport_cap_base", "transport_cap_base IN ('SalaryOnly','SalaryAndSalaryIncome','SalaryAndPaidAbsences')");
                    table.CheckConstraint("CK_company_settings_transport_frequency", "transport_frequency IN ('Biweekly','Monthly')");
                });

            migrationBuilder.CreateTable(
                name: "concepts",
                schema: "payroll",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    default_salary_constitutive = table.Column<bool>(type: "boolean", nullable: true),
                    multiplier = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_concepts", x => x.id);
                    table.CheckConstraint("CK_concepts_kind", "kind IN ('Overtime','Income','Deduction')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_company_settings_tenant_company",
                schema: "payroll",
                table: "company_settings",
                columns: new[] { "tenant_id", "company_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_concepts_tenant_company_kind_normalized_name",
                schema: "payroll",
                table: "concepts",
                columns: new[] { "tenant_id", "company_id", "kind", "normalized_name" },
                unique: true);

            // FK real a la empresa (R-19): una empresa con configuración de nómina no se borra; CompaniesUnitOfWork traduce el
            // RESTRICT a 422 companies.company.in_use. A mano porque EF no modela relaciones entre DbContext de módulos distintos
            // (mismo precedente que pos y hr).
            migrationBuilder.AddForeignKey(
                name: "FK_company_settings_companies_company_id",
                schema: "payroll",
                table: "company_settings",
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
                name: "FK_company_settings_companies_company_id",
                schema: "payroll",
                table: "company_settings");

            migrationBuilder.DropTable(
                name: "company_settings",
                schema: "payroll");

            migrationBuilder.DropTable(
                name: "concepts",
                schema: "payroll");
        }
    }
}
