using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportInternalTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_tickets_intake",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tickets_reply_channel",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_replies_channel",
                schema: "support",
                table: "ticket_replies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events");

            migrationBuilder.AddColumn<Guid>(
                name: "created_by_member_id",
                schema: "support",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_internal",
                schema: "support",
                table: "tickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_internal",
                schema: "support",
                table: "ticket_types",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_tickets_created_by",
                schema: "support",
                table: "tickets",
                columns: new[] { "tenant_id", "created_by_member_id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_tickets_intake",
                schema: "support",
                table: "tickets",
                sql: "intake IN ('Manual','Conversation','PublicForm','Internal')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tickets_internal",
                schema: "support",
                table: "tickets",
                sql: "(NOT is_internal OR (customer_id IS NULL AND conversation_id IS NULL AND manual_medium IS NULL AND NOT is_anonymous AND requester_first_names IS NULL AND requester_last_names IS NULL AND requester_business_name IS NULL AND requester_document_type IS NULL AND requester_document_number IS NULL AND requester_email IS NULL AND requester_mobile_phone IS NULL AND requester_landline_phone IS NULL AND requester_country_code IS NULL AND requester_address IS NULL AND requester_city_id IS NULL AND requester_city_name IS NULL)) AND ((reply_channel = 'None') = is_internal)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tickets_reply_channel",
                schema: "support",
                table: "tickets",
                sql: "reply_channel IN ('Thread','WhatsAppTemplate','Email','Portal','None')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ticket_types_internal",
                schema: "support",
                table: "ticket_types",
                sql: "NOT is_internal OR (term_business_days IS NULL AND NOT term_is_legal AND NOT requires_formal_response AND NOT is_published AND is_active)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ticket_replies_channel",
                schema: "support",
                table: "ticket_replies",
                sql: "channel IN ('Thread','WhatsAppTemplate','Email','Portal','None')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events",
                sql: "kind IN ('Created','StatusChanged','AssigneeChanged','TypeChanged','CustomerChanged','MessagePinned','MessageReleased','MessageClassified','MessageDismissed','FollowThreadChanged','ReplySent','ConversationLinked','Extended','PriorityChanged','PinReissued','DueDateChanged')");

            // Los índices por expresión o parciales van por SQL: EF no los modela (precedente InitialSupport). El de nombre pasa a ser
            // parcial (decisión D8): un tipo propio llamado «Tarea interna» no impide sembrar el interno, y es una relajación, así que
            // un pod viejo no se entera.
            migrationBuilder.Sql("DROP INDEX IF EXISTS support.\"IX_ticket_types_tenant_name\";");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_ticket_types_tenant_name\" ON support.ticket_types (tenant_id, lower(name)) WHERE NOT is_internal;");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"UX_ticket_types_tenant_internal\" ON support.ticket_types (tenant_id) WHERE is_internal;");

            // Backfill. Quien radicó cada ticket es el actor de su evento Created (null en el formulario público). Cada tenant ya
            // aprovisionado recibe su tipo interno detrás del último; los que se aprovisionen después lo siembra EnsureAsync.
            migrationBuilder.Sql(
                """
                UPDATE support.tickets AS t
                SET created_by_member_id = e.actor_member_id
                FROM support.ticket_events AS e
                WHERE e.ticket_id = t.id AND e.kind = 'Created' AND e.actor_member_id IS NOT NULL;
                """);
            migrationBuilder.Sql(
                """
                INSERT INTO support.ticket_types (id, tenant_id, name, term_business_days, term_is_legal, requires_formal_response,
                    is_published, is_active, is_internal, position, created_at, updated_at, version)
                SELECT gen_random_uuid(), s.tenant_id, 'Tarea interna', NULL, false, false, false, true, true,
                    COALESCE((SELECT max(ty.position) FROM support.ticket_types AS ty WHERE ty.tenant_id = s.tenant_id), 0) + 1,
                    now(), now(), 1
                FROM support.tenant_settings AS s
                WHERE NOT EXISTS (
                    SELECT 1 FROM support.ticket_types AS ty WHERE ty.tenant_id = s.tenant_id AND ty.is_internal);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Los tipos internos sin tickets se borran; con tareas ya radicadas la reversión se detiene en los CHECK de enums de más
            // abajo (una tarea no cabe en el esquema anterior): hay que borrarlas antes.
            migrationBuilder.Sql("DROP INDEX IF EXISTS support.\"UX_ticket_types_tenant_internal\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS support.\"IX_ticket_types_tenant_name\";");
            migrationBuilder.Sql(
                """
                DELETE FROM support.ticket_types AS ty
                WHERE ty.is_internal AND NOT EXISTS (SELECT 1 FROM support.tickets AS t WHERE t.ticket_type_id = ty.id);
                """);

            migrationBuilder.DropIndex(
                name: "IX_tickets_created_by",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tickets_intake",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tickets_internal",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tickets_reply_channel",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_types_internal",
                schema: "support",
                table: "ticket_types");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_replies_channel",
                schema: "support",
                table: "ticket_replies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events");

            migrationBuilder.DropColumn(
                name: "created_by_member_id",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "is_internal",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "is_internal",
                schema: "support",
                table: "ticket_types");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tickets_intake",
                schema: "support",
                table: "tickets",
                sql: "intake IN ('Manual','Conversation','PublicForm')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tickets_reply_channel",
                schema: "support",
                table: "tickets",
                sql: "reply_channel IN ('Thread','WhatsAppTemplate','Email','Portal')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ticket_replies_channel",
                schema: "support",
                table: "ticket_replies",
                sql: "channel IN ('Thread','WhatsAppTemplate','Email','Portal')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ticket_events_kind",
                schema: "support",
                table: "ticket_events",
                sql: "kind IN ('Created','StatusChanged','AssigneeChanged','TypeChanged','CustomerChanged','MessagePinned','MessageReleased','MessageClassified','MessageDismissed','FollowThreadChanged','ReplySent','ConversationLinked','Extended','PriorityChanged','PinReissued')");

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_ticket_types_tenant_name\" ON support.ticket_types (tenant_id, lower(name));");
        }
    }
}
