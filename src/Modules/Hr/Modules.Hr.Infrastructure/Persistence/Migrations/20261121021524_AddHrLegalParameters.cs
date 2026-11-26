using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Hr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHrLegalParameters : Migration
    {
        /// <summary>Crea la tabla y siembra los valores legales de 2024 a 2026 (spec 2026-11-20).</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "hr");

            migrationBuilder.CreateTable(
                name: "legal_parameters",
                schema: "hr",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legal_parameters", x => new { x.key, x.effective_from });
                    table.CheckConstraint("CK_legal_parameters_key", "key IN ('Smmlv','IntegralSalaryFloor')");
                });

            // Spec 2026-11-20 (RRHH fase 1), «Valores legales»: la carga de cada año es otra migración igual, no HasData.
            // Estos literales viven sólo acá y en las pruebas: el código resuelve de la tabla a la fecha que evalúa.
            migrationBuilder.InsertData(
                schema: "hr",
                table: "legal_parameters",
                columns: new[] { "key", "effective_from", "value" },
                values: new object[,]
                {
                    { "Smmlv", new DateOnly(2024, 1, 1), 1300000m },
                    { "IntegralSalaryFloor", new DateOnly(2024, 1, 1), 16900000m },
                    { "Smmlv", new DateOnly(2025, 1, 1), 1423500m },
                    { "IntegralSalaryFloor", new DateOnly(2025, 1, 1), 18505500m },
                    { "Smmlv", new DateOnly(2026, 1, 1), 1750905m },
                    { "IntegralSalaryFloor", new DateOnly(2026, 1, 1), 22761765m },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "hr",
                table: "legal_parameters",
                keyColumns: new[] { "key", "effective_from" },
                keyValues: new object[,]
                {
                    { "Smmlv", new DateOnly(2024, 1, 1) },
                    { "IntegralSalaryFloor", new DateOnly(2024, 1, 1) },
                    { "Smmlv", new DateOnly(2025, 1, 1) },
                    { "IntegralSalaryFloor", new DateOnly(2025, 1, 1) },
                    { "Smmlv", new DateOnly(2026, 1, 1) },
                    { "IntegralSalaryFloor", new DateOnly(2026, 1, 1) },
                });

            migrationBuilder.DropTable(
                name: "legal_parameters",
                schema: "hr");
        }
    }
}
