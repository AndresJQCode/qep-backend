using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRetiredTemplateHeaderImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "message_template_retired_header_images",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_template_retired_header_images", x => x.id);
                    table.ForeignKey(
                        name: "FK_message_template_retired_header_images_message_templates_te~",
                        column: x => x.template_id,
                        principalSchema: "messaging",
                        principalTable: "message_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_message_template_retired_header_images_template_id",
                schema: "messaging",
                table: "message_template_retired_header_images",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "UX_message_template_retired_header_images_file",
                schema: "messaging",
                table: "message_template_retired_header_images",
                column: "file_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_template_retired_header_images",
                schema: "messaging");
        }
    }
}
