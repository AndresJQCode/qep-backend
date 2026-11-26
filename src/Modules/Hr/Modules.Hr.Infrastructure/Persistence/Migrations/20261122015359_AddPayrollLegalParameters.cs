using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Hr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPayrollLegalParameters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_legal_parameters_key",
                schema: "hr",
                table: "legal_parameters");

            migrationBuilder.AlterColumn<decimal>(
                name: "value",
                schema: "hr",
                table: "legal_parameters",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.CreateTable(
                name: "legal_parameter_brackets",
                schema: "hr",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    lower_bound = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    upper_bound = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                    rate = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legal_parameter_brackets", x => new { x.key, x.effective_from, x.lower_bound });
                    table.CheckConstraint("CK_legal_parameter_brackets_key", "key IN ('SolidarityFund')");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_legal_parameters_key",
                schema: "hr",
                table: "legal_parameters",
                sql: "key IN ('Smmlv','IntegralSalaryFloor','TransportAllowance','ConnectivityAllowanceSince','HealthEmployeeRate','HealthEmployerRate','PensionEmployeeRate','PensionEmployerRate','HighRiskPensionTotalRate','ArlClassIRate','ArlClassIIRate','ArlClassIIIRate','ArlClassIVRate','ArlClassVRate','CompensationFundRate','IcbfRate','SenaRate','SeveranceProvisionRate','PremiumProvisionRate','VacationProvisionRate','SeveranceInterestProvisionRate','TransportAllowanceCapSmmlv','NonSalaryCapRate','ExonerationThresholdSmmlv','IbcCapSmmlv','SundaySurchargeRate','WeeklyHours','ExtraDayRate','ExtraNightRate','NightSurchargeRate')");

            // Spec 2026-11-21 (nómina), «Valores legales: claves y siembra»: la carga de cada año es otra migración igual, no
            // HasData. Estos literales viven sólo acá y en las pruebas: el código resuelve de la tabla a la fecha que evalúa.
            migrationBuilder.InsertData(
                schema: "hr",
                table: "legal_parameters",
                columns: new[] { "key", "effective_from", "value" },
                values: new object[,]
                {
                    { "TransportAllowance", new DateOnly(2025, 1, 1), 200000m },
                    { "TransportAllowance", new DateOnly(2026, 1, 1), 249095m },
                    { "HealthEmployeeRate", new DateOnly(2025, 1, 1), 0.04m },
                    { "HealthEmployeeRate", new DateOnly(2026, 1, 1), 0.04m },
                    { "HealthEmployerRate", new DateOnly(2025, 1, 1), 0.085m },
                    { "HealthEmployerRate", new DateOnly(2026, 1, 1), 0.085m },
                    { "PensionEmployeeRate", new DateOnly(2025, 1, 1), 0.04m },
                    { "PensionEmployeeRate", new DateOnly(2026, 1, 1), 0.04m },
                    { "PensionEmployerRate", new DateOnly(2025, 1, 1), 0.12m },
                    { "PensionEmployerRate", new DateOnly(2026, 1, 1), 0.12m },
                    { "HighRiskPensionTotalRate", new DateOnly(2025, 1, 1), 0.26m },
                    { "HighRiskPensionTotalRate", new DateOnly(2026, 1, 1), 0.26m },
                    { "ArlClassIRate", new DateOnly(2025, 1, 1), 0.00522m },
                    { "ArlClassIRate", new DateOnly(2026, 1, 1), 0.00522m },
                    { "ArlClassIIRate", new DateOnly(2025, 1, 1), 0.01044m },
                    { "ArlClassIIRate", new DateOnly(2026, 1, 1), 0.01044m },
                    { "ArlClassIIIRate", new DateOnly(2025, 1, 1), 0.02436m },
                    { "ArlClassIIIRate", new DateOnly(2026, 1, 1), 0.02436m },
                    { "ArlClassIVRate", new DateOnly(2025, 1, 1), 0.0435m },
                    { "ArlClassIVRate", new DateOnly(2026, 1, 1), 0.0435m },
                    { "ArlClassVRate", new DateOnly(2025, 1, 1), 0.0696m },
                    { "ArlClassVRate", new DateOnly(2026, 1, 1), 0.0696m },
                    { "CompensationFundRate", new DateOnly(2025, 1, 1), 0.04m },
                    { "CompensationFundRate", new DateOnly(2026, 1, 1), 0.04m },
                    { "IcbfRate", new DateOnly(2025, 1, 1), 0.03m },
                    { "IcbfRate", new DateOnly(2026, 1, 1), 0.03m },
                    { "SenaRate", new DateOnly(2025, 1, 1), 0.02m },
                    { "SenaRate", new DateOnly(2026, 1, 1), 0.02m },
                    { "SeveranceProvisionRate", new DateOnly(2025, 1, 1), 0.0833m },
                    { "SeveranceProvisionRate", new DateOnly(2026, 1, 1), 0.0833m },
                    { "PremiumProvisionRate", new DateOnly(2025, 1, 1), 0.0833m },
                    { "PremiumProvisionRate", new DateOnly(2026, 1, 1), 0.0833m },
                    { "VacationProvisionRate", new DateOnly(2025, 1, 1), 0.0417m },
                    { "VacationProvisionRate", new DateOnly(2026, 1, 1), 0.0417m },
                    { "SeveranceInterestProvisionRate", new DateOnly(2025, 1, 1), 0.01m },
                    { "SeveranceInterestProvisionRate", new DateOnly(2026, 1, 1), 0.01m },
                    { "TransportAllowanceCapSmmlv", new DateOnly(2025, 1, 1), 2m },
                    { "TransportAllowanceCapSmmlv", new DateOnly(2026, 1, 1), 2m },
                    { "NonSalaryCapRate", new DateOnly(2025, 1, 1), 0.40m },
                    { "NonSalaryCapRate", new DateOnly(2026, 1, 1), 0.40m },
                    { "ExonerationThresholdSmmlv", new DateOnly(2025, 1, 1), 10m },
                    { "ExonerationThresholdSmmlv", new DateOnly(2026, 1, 1), 10m },
                    { "IbcCapSmmlv", new DateOnly(2025, 1, 1), 25m },
                    { "IbcCapSmmlv", new DateOnly(2026, 1, 1), 25m },
                    { "ExtraDayRate", new DateOnly(2025, 1, 1), 0.25m },
                    { "ExtraDayRate", new DateOnly(2026, 1, 1), 0.25m },
                    { "ExtraNightRate", new DateOnly(2025, 1, 1), 0.75m },
                    { "ExtraNightRate", new DateOnly(2026, 1, 1), 0.75m },
                    { "NightSurchargeRate", new DateOnly(2025, 1, 1), 0.35m },
                    { "NightSurchargeRate", new DateOnly(2026, 1, 1), 0.35m },
                    { "ConnectivityAllowanceSince", new DateOnly(2025, 6, 25), 1m },
                    { "SundaySurchargeRate", new DateOnly(2025, 7, 1), 0.80m },
                    { "SundaySurchargeRate", new DateOnly(2026, 7, 1), 0.90m },
                    { "SundaySurchargeRate", new DateOnly(2027, 7, 1), 1.00m },
                    { "WeeklyHours", new DateOnly(2023, 7, 15), 47m },
                    { "WeeklyHours", new DateOnly(2024, 7, 15), 46m },
                    { "WeeklyHours", new DateOnly(2025, 7, 15), 44m },
                    { "WeeklyHours", new DateOnly(2026, 7, 15), 42m },
                });

            migrationBuilder.InsertData(
                schema: "hr",
                table: "legal_parameter_brackets",
                columns: new[] { "key", "effective_from", "lower_bound", "upper_bound", "rate" },
                values: new object[,]
                {
                    { "SolidarityFund", new DateOnly(2025, 1, 1), 4m, 16m, 0.01m },
                    { "SolidarityFund", new DateOnly(2025, 1, 1), 16m, 17m, 0.012m },
                    { "SolidarityFund", new DateOnly(2025, 1, 1), 17m, 18m, 0.014m },
                    { "SolidarityFund", new DateOnly(2025, 1, 1), 18m, 19m, 0.016m },
                    { "SolidarityFund", new DateOnly(2025, 1, 1), 19m, 20m, 0.018m },
                    { "SolidarityFund", new DateOnly(2025, 1, 1), 20m, 25m, 0.02m },
                    { "SolidarityFund", new DateOnly(2026, 1, 1), 4m, 16m, 0.01m },
                    { "SolidarityFund", new DateOnly(2026, 1, 1), 16m, 17m, 0.012m },
                    { "SolidarityFund", new DateOnly(2026, 1, 1), 17m, 18m, 0.014m },
                    { "SolidarityFund", new DateOnly(2026, 1, 1), 18m, 19m, 0.016m },
                    { "SolidarityFund", new DateOnly(2026, 1, 1), 19m, 20m, 0.018m },
                    { "SolidarityFund", new DateOnly(2026, 1, 1), 20m, 25m, 0.02m },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Las filas de nómina no caben en el CHECK de dos claves: se borran antes (las de la fase 1 quedan intactas).
            migrationBuilder.Sql("DELETE FROM hr.legal_parameters WHERE key NOT IN ('Smmlv','IntegralSalaryFloor');");

            migrationBuilder.DropTable(
                name: "legal_parameter_brackets",
                schema: "hr");

            migrationBuilder.DropCheckConstraint(
                name: "CK_legal_parameters_key",
                schema: "hr",
                table: "legal_parameters");

            migrationBuilder.AlterColumn<decimal>(
                name: "value",
                schema: "hr",
                table: "legal_parameters",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)",
                oldPrecision: 18,
                oldScale: 6);

            migrationBuilder.AddCheckConstraint(
                name: "CK_legal_parameters_key",
                schema: "hr",
                table: "legal_parameters",
                sql: "key IN ('Smmlv','IntegralSalaryFloor')");
        }
    }
}
