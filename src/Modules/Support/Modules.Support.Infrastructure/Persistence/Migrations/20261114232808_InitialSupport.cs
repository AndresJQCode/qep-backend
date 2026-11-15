using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "support");

            migrationBuilder.CreateTable(
                name: "tenant_settings",
                schema: "support",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    due_soon_business_days = table.Column<int>(type: "integer", nullable: false, defaultValue: 3),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_ticket_number = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_settings", x => x.tenant_id);
                    table.CheckConstraint("CK_tenant_settings_due_soon", "due_soon_business_days BETWEEN 0 AND 30");
                });

            migrationBuilder.CreateTable(
                name: "ticket_types",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    term_business_days = table.Column<int>(type: "integer", nullable: true),
                    term_is_legal = table.Column<bool>(type: "boolean", nullable: false),
                    requires_formal_response = table.Column<bool>(type: "boolean", nullable: false),
                    is_published = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_types", x => x.id);
                    table.CheckConstraint("CK_ticket_types_legal_term", "NOT term_is_legal OR term_business_days IS NOT NULL");
                    table.CheckConstraint("CK_ticket_types_term", "term_business_days IS NULL OR term_business_days BETWEEN 1 AND 365");
                });

            migrationBuilder.CreateTable(
                name: "tickets",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    ticket_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    term_business_days = table.Column<int>(type: "integer", nullable: true),
                    term_is_legal = table.Column<bool>(type: "boolean", nullable: false),
                    requires_formal_response = table.Column<bool>(type: "boolean", nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    intake = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    manual_medium = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    reply_channel = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    assignee_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requester_first_names = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    requester_last_names = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    requester_business_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    requester_document_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    requester_document_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    requester_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    requester_mobile_phone = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    requester_landline_phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    requester_country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    requester_address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_on = table.Column<DateOnly>(type: "date", nullable: false),
                    due_on = table.Column<DateOnly>(type: "date", nullable: true),
                    paused_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paused_business_days = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    resolution_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_event_ordinal = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tickets", x => x.id);
                    table.CheckConstraint("CK_tickets_intake", "intake IN ('Manual','Conversation','PublicForm')");
                    table.CheckConstraint("CK_tickets_reply_channel", "reply_channel IN ('Thread','WhatsAppTemplate','Email')");
                    table.CheckConstraint("CK_tickets_status", "status IN ('New','Open','WaitingOnCustomer','WaitingInternal','Resolved','Closed')");
                });

            migrationBuilder.CreateTable(
                name: "ticket_events",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    actor_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_ticket_events_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "support",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_events_ticket_ordinal",
                schema: "support",
                table: "ticket_events",
                columns: new[] { "ticket_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tickets_tenant_customer",
                schema: "support",
                table: "tickets",
                columns: new[] { "tenant_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_tenant_due",
                schema: "support",
                table: "tickets",
                columns: new[] { "tenant_id", "due_on" });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_tenant_number",
                schema: "support",
                table: "tickets",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tickets_tenant_status_received",
                schema: "support",
                table: "tickets",
                columns: new[] { "tenant_id", "status", "received_at" },
                descending: new[] { false, false, true });

            // Plan D11: el nombre de un tipo es único por tenant sin distinguir mayúsculas. EF no modela índices por expresión, así
            // que va por SQL (precedente AddAutomations); SupportUnitOfWork lo traduce por nombre a support.ticket_type.name_taken.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_ticket_types_tenant_name\" ON support.ticket_types (tenant_id, lower(name));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS support.\"IX_ticket_types_tenant_name\";");

            migrationBuilder.DropTable(
                name: "tenant_settings",
                schema: "support");

            migrationBuilder.DropTable(
                name: "ticket_events",
                schema: "support");

            migrationBuilder.DropTable(
                name: "ticket_types",
                schema: "support");

            migrationBuilder.DropTable(
                name: "tickets",
                schema: "support");
        }
    }
}
