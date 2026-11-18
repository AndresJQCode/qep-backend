using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateHeaderImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "header_image_file_id",
                schema: "messaging",
                table: "message_templates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "header_image_url",
                schema: "messaging",
                table: "message_templates",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_message_templates_header_image",
                schema: "messaging",
                table: "message_templates",
                column: "header_image_file_id",
                unique: true,
                filter: "header_image_file_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_message_templates_header_image",
                schema: "messaging",
                table: "message_templates");

            migrationBuilder.DropColumn(
                name: "header_image_file_id",
                schema: "messaging",
                table: "message_templates");

            migrationBuilder.DropColumn(
                name: "header_image_url",
                schema: "messaging",
                table: "message_templates");
        }
    }
}
