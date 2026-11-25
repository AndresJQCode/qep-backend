using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Hr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedHistoricalSalaryFloors : Migration
    {
        // SMMLV de cada decreto y piso integral = 13 × SMMLV (10 SMMLV más el 30 % de factor prestacional). ON CONFLICT porque
        // la consola de operador pudo haber cargado antes alguna de estas vigencias: gana lo que ya está guardado.
        private const string SeedSql = """
            INSERT INTO hr.legal_parameters (key, effective_from, value) VALUES
                ('Smmlv', '2017-01-01', 737717),
                ('IntegralSalaryFloor', '2017-01-01', 9590321),
                ('Smmlv', '2018-01-01', 781242),
                ('IntegralSalaryFloor', '2018-01-01', 10156146),
                ('Smmlv', '2019-01-01', 828116),
                ('IntegralSalaryFloor', '2019-01-01', 10765508),
                ('Smmlv', '2020-01-01', 877803),
                ('IntegralSalaryFloor', '2020-01-01', 11411439),
                ('Smmlv', '2021-01-01', 908526),
                ('IntegralSalaryFloor', '2021-01-01', 11810838),
                ('Smmlv', '2022-01-01', 1000000),
                ('IntegralSalaryFloor', '2022-01-01', 13000000),
                ('Smmlv', '2023-01-01', 1160000),
                ('IntegralSalaryFloor', '2023-01-01', 15080000)
            ON CONFLICT (key, effective_from) DO NOTHING;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SeedSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Sólo las filas sembradas (sin autor): lo que cargó un operador se queda.
            migrationBuilder.Sql(
                """
                DELETE FROM hr.legal_parameters
                WHERE key IN ('Smmlv', 'IntegralSalaryFloor') AND effective_from < '2024-01-01' AND updated_at IS NULL;
                """);
        }
    }
}
