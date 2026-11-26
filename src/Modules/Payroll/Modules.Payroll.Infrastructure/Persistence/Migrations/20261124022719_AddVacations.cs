using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Payroll.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVacations : Migration
    {
        /// <inheritdoc />
        /// <summary>
        /// Everything here is additive (expand/contract): pods still on the previous version keep working. The EXCLUDE constraint is the
        /// guarantee behind the handler's overlap check, which cannot see a concurrent insert; <c>btree_gist</c> lets it combine «same
        /// employee» (equality) with «date ranges that overlap». Payroll migrates before Scheduling in <c>Program.cs</c>, so the
        /// extension is created here too (<c>IF NOT EXISTS</c>). EF does not model EXCLUDE, so it is added by hand, like
        /// <c>InitialScheduling</c> does.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

            migrationBuilder.AddColumn<int>(
                name: "vacation_days",
                schema: "payroll",
                table: "employee_results",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "vacation_photos",
                schema: "payroll",
                table: "employee_results",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.CreateTable(
                name: "vacations",
                schema: "payroll",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    return_date = table.Column<DateOnly>(type: "date", nullable: false),
                    business_days = table.Column<int>(type: "integer", nullable: false),
                    previous_month_ibc = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    external_months = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vacations", x => x.id);
                    table.CheckConstraint("CK_vacations_origin", "origin IN ('Qep','External')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_vacations_tenant_company_employee_start",
                schema: "payroll",
                table: "vacations",
                columns: new[] { "tenant_id", "company_id", "employee_id", "start_date" });

            // FK real a la empresa (R-19): una empresa con vacaciones no se borra; CompaniesUnitOfWork traduce el RESTRICT a 422
            // companies.company.in_use. A mano porque EF no modela relaciones entre DbContext de módulos distintos.
            migrationBuilder.AddForeignKey(
                name: "FK_vacations_companies_company_id",
                schema: "payroll",
                table: "vacations",
                column: "company_id",
                principalSchema: "companies",
                principalTable: "companies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                ALTER TABLE payroll.vacations
                    ADD CONSTRAINT "EX_vacations_no_overlap"
                    EXCLUDE USING gist (
                        tenant_id WITH =, company_id WITH =, employee_id WITH =,
                        daterange(start_date, end_date, '[]') WITH &&);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vacations",
                schema: "payroll");

            migrationBuilder.DropColumn(
                name: "vacation_days",
                schema: "payroll",
                table: "employee_results");

            migrationBuilder.DropColumn(
                name: "vacation_photos",
                schema: "payroll",
                table: "employee_results");
        }
    }
}
