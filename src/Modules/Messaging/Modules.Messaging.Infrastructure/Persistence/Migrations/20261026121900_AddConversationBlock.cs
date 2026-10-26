using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationBlock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "blocked_at",
                schema: "messaging",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "blocked_by_member_id",
                schema: "messaging",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "blocked_wa_id",
                schema: "messaging",
                table: "conversations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_block",
                schema: "messaging",
                table: "conversations",
                sql: "(blocked_at IS NULL AND blocked_by_member_id IS NULL AND blocked_wa_id IS NULL) OR (blocked_at IS NOT NULL AND blocked_by_member_id IS NOT NULL AND blocked_wa_id IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_conversations_block",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "blocked_at",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "blocked_by_member_id",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "blocked_wa_id",
                schema: "messaging",
                table: "conversations");
        }
    }
}
