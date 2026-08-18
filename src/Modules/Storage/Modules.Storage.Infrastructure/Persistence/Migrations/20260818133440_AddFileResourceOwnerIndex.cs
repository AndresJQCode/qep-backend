using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Storage.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFileResourceOwnerIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_file_resources_tenant_owner",
                schema: "storage",
                table: "file_resources",
                columns: new[] { "tenant_id", "owner_type", "owner_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_file_resources_tenant_owner",
                schema: "storage",
                table: "file_resources");
        }
    }
}
