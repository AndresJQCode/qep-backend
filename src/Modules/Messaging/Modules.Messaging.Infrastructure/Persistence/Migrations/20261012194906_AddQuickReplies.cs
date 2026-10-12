using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuickReplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "quick_replies",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shortcut = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    body = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quick_replies", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_quick_replies_tenant_shortcut",
                schema: "messaging",
                table: "quick_replies",
                columns: new[] { "tenant_id", "shortcut" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quick_replies",
                schema: "messaging");
        }
    }
}
