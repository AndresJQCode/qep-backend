using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportFormalNextStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "formal_next_step",
                schema: "support",
                table: "ticket_types",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "formal_next_step",
                schema: "support",
                table: "tenant_settings",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "formal_next_step",
                schema: "support",
                table: "ticket_types");

            migrationBuilder.DropColumn(
                name: "formal_next_step",
                schema: "support",
                table: "tenant_settings");
        }
    }
}
