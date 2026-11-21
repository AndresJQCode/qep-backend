using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Hr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropEmployeeCompensationFund : Migration
    {
        /// <inheritdoc />
        // Expand/contract: Hr es nuevo (AddHrEmployees es del 2026-11-21 y no se ha desplegado), así que ningún pod viejo escribe la columna.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "compensation_fund",
                schema: "hr",
                table: "employees");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "compensation_fund",
                schema: "hr",
                table: "employees",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
        }
    }
}
