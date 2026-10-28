using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Automations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WidenHttpCallOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "outcome",
                schema: "automations",
                table: "http_calls",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "outcome",
                schema: "automations",
                table: "http_calls",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(24)",
                oldMaxLength: 24);
        }
    }
}
