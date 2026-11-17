using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Storage.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFileResourceTenantOwnerCreatedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_file_resources_tenant_owner_created",
                schema: "storage",
                table: "file_resources",
                columns: new[] { "tenant_id", "owner_type", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_file_resources_tenant_owner_created",
                schema: "storage",
                table: "file_resources");
        }
    }
}
