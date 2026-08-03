using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Storage.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFileMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "category",
                schema: "storage",
                table: "file_resources",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "tags",
                schema: "storage",
                table: "file_resources",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.CreateIndex(
                name: "IX_file_resources_tags",
                schema: "storage",
                table: "file_resources",
                column: "tags")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_file_resources_tenant_id_category",
                schema: "storage",
                table: "file_resources",
                columns: new[] { "tenant_id", "category" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_file_resources_tags",
                schema: "storage",
                table: "file_resources");

            migrationBuilder.DropIndex(
                name: "IX_file_resources_tenant_id_category",
                schema: "storage",
                table: "file_resources");

            migrationBuilder.DropColumn(
                name: "category",
                schema: "storage",
                table: "file_resources");

            migrationBuilder.DropColumn(
                name: "tags",
                schema: "storage",
                table: "file_resources");
        }
    }
}
