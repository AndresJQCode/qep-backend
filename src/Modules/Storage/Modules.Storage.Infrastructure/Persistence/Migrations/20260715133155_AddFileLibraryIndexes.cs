using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Storage.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFileLibraryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_file_resources_tenant_id_status_created_at_id",
                schema: "storage",
                table: "file_resources",
                columns: new[] { "tenant_id", "status", "created_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_file_resources_tenant_id_status_created_at_id",
                schema: "storage",
                table: "file_resources");
        }
    }
}
