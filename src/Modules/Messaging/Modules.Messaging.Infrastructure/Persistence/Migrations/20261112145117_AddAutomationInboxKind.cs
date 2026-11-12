using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationInboxKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "messaging",
                table: "automation_inbox",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Engine");

            migrationBuilder.AddCheckConstraint(
                name: "CK_automation_inbox_kind",
                schema: "messaging",
                table: "automation_inbox",
                sql: "kind IN ('Engine','ReleaseRun')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_automation_inbox_kind",
                schema: "messaging",
                table: "automation_inbox");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "messaging",
                table: "automation_inbox");
        }
    }
}
