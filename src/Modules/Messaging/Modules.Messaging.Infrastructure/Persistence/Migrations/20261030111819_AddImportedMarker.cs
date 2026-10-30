using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImportedMarker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "imported",
                schema: "messaging",
                table: "messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "imported",
                schema: "messaging",
                table: "conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "started_by_echo",
                schema: "messaging",
                table: "conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_conversations_tenant_imported",
                schema: "messaging",
                table: "conversations",
                column: "tenant_id",
                filter: "imported");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_conversations_tenant_imported",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "imported",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "imported",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "started_by_echo",
                schema: "messaging",
                table: "conversations");
        }
    }
}
