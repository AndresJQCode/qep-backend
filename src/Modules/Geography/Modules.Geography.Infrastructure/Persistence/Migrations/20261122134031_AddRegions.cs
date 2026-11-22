using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Geography.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRegions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "regions",
                schema: "geography",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_regions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "seed_state",
                schema: "geography",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seed_state", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "region_cities",
                schema: "geography",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    region_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_region_cities", x => x.id);
                    table.ForeignKey(
                        name: "FK_region_cities_regions_region_id",
                        column: x => x.region_id,
                        principalSchema: "geography",
                        principalTable: "regions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_region_cities_region_id_code",
                schema: "geography",
                table: "region_cities",
                columns: new[] { "region_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_regions_country_code_code",
                schema: "geography",
                table: "regions",
                columns: new[] { "country_code", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "region_cities",
                schema: "geography");

            migrationBuilder.DropTable(
                name: "seed_state",
                schema: "geography");

            migrationBuilder.DropTable(
                name: "regions",
                schema: "geography");
        }
    }
}
