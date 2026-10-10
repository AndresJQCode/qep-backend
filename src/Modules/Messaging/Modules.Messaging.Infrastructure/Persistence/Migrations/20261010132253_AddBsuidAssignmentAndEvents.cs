using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBsuidAssignmentAndEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_direction",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_kind",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_conversations_connection_wa",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.AddColumn<Guid>(
                name: "reply_to_message_id",
                schema: "messaging",
                table: "messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reply_to_wamid",
                schema: "messaging",
                table: "messages",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "wa_id",
                schema: "messaging",
                table: "conversations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "assigned_at",
                schema: "messaging",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "assigned_member_id",
                schema: "messaging",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "customer_id",
                schema: "messaging",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "parent_user_id",
                schema: "messaging",
                table: "conversations",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "user_id",
                schema: "messaging",
                table: "conversations",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "username",
                schema: "messaging",
                table: "conversations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_direction",
                schema: "messaging",
                table: "messages",
                sql: "direction IN (1, 2, 3)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_event_shape",
                schema: "messaging",
                table: "messages",
                sql: "direction <> 3 OR (wamid IS NULL AND client_id IS NULL AND status = 2 AND details IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_kind",
                schema: "messaging",
                table: "messages",
                sql: "kind BETWEEN 1 AND 13");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_system_is_event",
                schema: "messaging",
                table: "messages",
                sql: "(direction = 3) = (kind = 13)");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_connection_user",
                schema: "messaging",
                table: "conversations",
                columns: new[] { "connection_id", "user_id" },
                unique: true,
                filter: "user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_connection_wa_legacy",
                schema: "messaging",
                table: "conversations",
                columns: new[] { "connection_id", "wa_id" },
                unique: true,
                filter: "user_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_tenant_assignee_status_activity",
                schema: "messaging",
                table: "conversations",
                columns: new[] { "tenant_id", "assigned_member_id", "status", "last_activity_at", "id" },
                descending: new[] { false, false, false, true, true },
                filter: "assigned_member_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_tenant_customer_activity",
                schema: "messaging",
                table: "conversations",
                columns: new[] { "tenant_id", "customer_id", "last_activity_at" },
                descending: new[] { false, false, true },
                filter: "customer_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_tenant_unassigned_status_activity",
                schema: "messaging",
                table: "conversations",
                columns: new[] { "tenant_id", "status", "last_activity_at", "id" },
                descending: new[] { false, false, true, true },
                filter: "assigned_member_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_username_trgm",
                schema: "messaging",
                table: "conversations",
                column: "username")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_assignment",
                schema: "messaging",
                table: "conversations",
                sql: "(assigned_member_id IS NULL) = (assigned_at IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_identity",
                schema: "messaging",
                table: "conversations",
                sql: "user_id IS NOT NULL OR wa_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Spec 2026-10-10 §7.1: lo que el esquema viejo no admite. Primero las filas, después las restricciones.
            migrationBuilder.Sql("DELETE FROM messaging.messages WHERE direction = 3;");            // CK_messages_direction / kind
            migrationBuilder.Sql("DELETE FROM messaging.conversations WHERE wa_id IS NULL;");        // wa_id NOT NULL (cascada a messages)
            migrationBuilder.Sql("""
                DELETE FROM messaging.conversations c
                WHERE EXISTS (SELECT 1 FROM messaging.conversations o
                              WHERE o.connection_id = c.connection_id AND o.wa_id = c.wa_id AND o.id < c.id);
                """);                                                                                  // IX_conversations_connection_wa único

            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_direction",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_event_shape",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_kind",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_messages_system_is_event",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_conversations_connection_user",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "IX_conversations_connection_wa_legacy",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "IX_conversations_tenant_assignee_status_activity",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "IX_conversations_tenant_customer_activity",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "IX_conversations_tenant_unassigned_status_activity",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "IX_conversations_username_trgm",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_conversations_assignment",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_conversations_identity",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "reply_to_message_id",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "reply_to_wamid",
                schema: "messaging",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "assigned_at",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "assigned_member_id",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "customer_id",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "parent_user_id",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "user_id",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "username",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.AlterColumn<string>(
                name: "wa_id",
                schema: "messaging",
                table: "conversations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_direction",
                schema: "messaging",
                table: "messages",
                sql: "direction IN (1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_messages_kind",
                schema: "messaging",
                table: "messages",
                sql: "kind BETWEEN 1 AND 12");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_connection_wa",
                schema: "messaging",
                table: "conversations",
                columns: new[] { "connection_id", "wa_id" },
                unique: true);
        }
    }
}
