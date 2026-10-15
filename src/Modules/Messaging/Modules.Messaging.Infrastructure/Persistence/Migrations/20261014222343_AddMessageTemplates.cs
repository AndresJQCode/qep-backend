using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "message_templates",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    meta_template_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    parameter_format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    components = table.Column<string>(type: "jsonb", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_by_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_templates", x => x.id);
                    table.CheckConstraint("CK_message_templates_kind", "kind IN ('Free','Quotation','External')");
                });

            migrationBuilder.CreateTable(
                name: "template_mirror_syncs",
                schema: "messaging",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_mirror_syncs", x => new { x.tenant_id, x.connection_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_message_templates_status",
                schema: "messaging",
                table: "message_templates",
                columns: new[] { "tenant_id", "connection_id", "status" });

            migrationBuilder.CreateIndex(
                name: "UX_message_templates_meta_id",
                schema: "messaging",
                table: "message_templates",
                columns: new[] { "tenant_id", "connection_id", "meta_template_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_message_templates_name_language",
                schema: "messaging",
                table: "message_templates",
                columns: new[] { "tenant_id", "connection_id", "name", "language" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_templates",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "template_mirror_syncs",
                schema: "messaging");
        }
    }
}
