using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Ai.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSourceTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source_template_id",
                schema: "ai",
                table: "agents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_template_id",
                schema: "ai",
                table: "agents");
        }
    }
}
