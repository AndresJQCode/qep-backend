using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Geography.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialGeography : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "geography");

            migrationBuilder.CreateTable(
                name: "departments",
                schema: "geography",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    divipola_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_departments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cities",
                schema: "geography",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    divipola_code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cities", x => x.id);
                    table.ForeignKey(
                        name: "FK_cities_departments_department_id",
                        column: x => x.department_id,
                        principalSchema: "geography",
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cities_department_id",
                schema: "geography",
                table: "cities",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "IX_cities_divipola_code",
                schema: "geography",
                table: "cities",
                column: "divipola_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_departments_divipola_code",
                schema: "geography",
                table: "departments",
                column: "divipola_code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cities",
                schema: "geography");

            migrationBuilder.DropTable(
                name: "departments",
                schema: "geography");
        }
    }
}
