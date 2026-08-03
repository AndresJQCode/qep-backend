using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Storage.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFileImageVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "file_variants",
                schema: "storage",
                columns: table => new
                {
                    file_resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_variants", x => new { x.file_resource_id, x.name });
                    table.ForeignKey(
                        name: "FK_file_variants_file_resources_file_resource_id",
                        column: x => x.file_resource_id,
                        principalSchema: "storage",
                        principalTable: "file_resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "file_variants",
                schema: "storage");
        }
    }
}
