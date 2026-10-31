using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyAuthor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_sent_by_kind",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_sent_by_one",
                schema: "messaging",
                table: "messages");

            migrationBuilder.AddColumn<Guid>(
                name: "sent_by_api_key_id",
                schema: "messaging",
                table: "messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sent_by_api_key_name",
                schema: "messaging",
                table: "messages",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_messages_tenant_api_client",
                schema: "messaging",
                table: "messages",
                columns: new[] { "tenant_id", "client_id" },
                unique: true,
                filter: "sent_by_kind = 4");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_sent_by_api_key",
                schema: "messaging",
                table: "messages",
                sql: "(sent_by_kind = 4) = (sent_by_api_key_id IS NOT NULL) AND (sent_by_api_key_id IS NULL) = (sent_by_api_key_name IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_sent_by_kind",
                schema: "messaging",
                table: "messages",
                sql: "sent_by_kind IN (1,2,4)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_sent_by_one",
                schema: "messaging",
                table: "messages",
                sql: "sent_by_kind NOT IN (2,4) OR sent_by_member_id IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_tenant_api_client",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_sent_by_api_key",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_sent_by_kind",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_sent_by_one",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "sent_by_api_key_id",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "sent_by_api_key_name",
                schema: "messaging",
                table: "messages");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_sent_by_kind",
                schema: "messaging",
                table: "messages",
                sql: "sent_by_kind IN (1,2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_sent_by_one",
                schema: "messaging",
                table: "messages",
                sql: "sent_by_kind <> 2 OR sent_by_member_id IS NULL");
        }
    }
}
