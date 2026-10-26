using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Automations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChatbotAttemptsExhausted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "exhausted_action",
                schema: "automations",
                table: "chatbots",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Restart");

            migrationBuilder.AddColumn<Guid>(
                name: "exhausted_member_id",
                schema: "automations",
                table: "chatbots",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "exhausted_action",
                schema: "automations",
                table: "chatbots");

            migrationBuilder.DropColumn(
                name: "exhausted_member_id",
                schema: "automations",
                table: "chatbots");
        }
    }
}
