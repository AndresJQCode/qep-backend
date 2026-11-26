using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportPublicForm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_tickets_reply_channel",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_replies_channel",
                schema: "support",
                table: "ticket_replies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_replies_delivery",
                schema: "support",
                table: "ticket_replies");

            migrationBuilder.AddColumn<Guid>(
                name: "form_version_id",
                schema: "support",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_anonymous",
                schema: "support",
                table: "tickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "requester_city_id",
                schema: "support",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requester_city_name",
                schema: "support",
                table: "tickets",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "consents",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_text_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_text_version = table.Column<int>(type: "integer", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    authorization_text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    policy_text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    policy_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    terms_text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    visitor_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "form_versions",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    is_published = table.Column<bool>(type: "boolean", nullable: false),
                    show_document = table.Column<bool>(type: "boolean", nullable: false),
                    show_address = table.Column<bool>(type: "boolean", nullable: false),
                    show_landline = table.Column<bool>(type: "boolean", nullable: false),
                    business_name_required = table.Column<bool>(type: "boolean", nullable: false),
                    accent_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    intro_text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    success_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_form_versions", x => x.id);
                    table.CheckConstraint("CK_form_versions_version", "version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "legal_text_versions",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    authorization_text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    policy_text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    policy_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    terms_text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legal_text_versions", x => x.id);
                    table.CheckConstraint("CK_legal_text_versions_policy", "policy_text IS NOT NULL OR policy_url IS NOT NULL");
                    table.CheckConstraint("CK_legal_text_versions_version", "version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "ticket_access",
                schema: "support",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pin_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pin_issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sent_channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    last_failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_access", x => x.ticket_id);
                    table.CheckConstraint("CK_ticket_access_failed_attempts", "failed_attempts >= 0");
                    table.ForeignKey(
                        name: "FK_ticket_access_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "support",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                name: "CK_ticket_replies_delivery",
                schema: "support",
                table: "ticket_replies",
                sql: "delivery IN ('Text','Template','Email','Portal')");

            migrationBuilder.CreateIndex(
                name: "IX_consents_ticket",
                schema: "support",
                table: "consents",
                column: "ticket_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_form_versions_created_by",
                schema: "support",
                table: "form_versions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_form_versions_tenant_version",
                schema: "support",
                table: "form_versions",
                columns: new[] { "tenant_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_legal_text_versions_created_by",
                schema: "support",
                table: "legal_text_versions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_legal_text_versions_tenant_version",
                schema: "support",
                table: "legal_text_versions",
                columns: new[] { "tenant_id", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // `consents` y `legal_text_versions` son prueba legal: este Down no las borra (precedente `customers.data_consents`, spec
            // §9.5, plan F17). Quedan huérfanas del modelo tras un rollback; volver a subir esta migración exige revisarlas a mano
            // antes, porque su CreateTable chocaría con ellas. Nunca se corre en producción: el rollback es revertir el código.
            migrationBuilder.DropTable(
                name: "form_versions",
                schema: "support");

            migrationBuilder.DropTable(
                name: "ticket_access",
                schema: "support");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tickets_reply_channel",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_replies_channel",
                schema: "support",
                table: "ticket_replies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ticket_replies_delivery",
                schema: "support",
                table: "ticket_replies");

            migrationBuilder.DropColumn(
                name: "form_version_id",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "is_anonymous",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "requester_city_id",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "requester_city_name",
                schema: "support",
                table: "tickets");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tickets_reply_channel",
                schema: "support",
                table: "tickets",
                sql: "reply_channel IN ('Thread','WhatsAppTemplate','Email')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ticket_replies_channel",
                schema: "support",
                table: "ticket_replies",
                sql: "channel IN ('Thread','WhatsAppTemplate','Email')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ticket_replies_delivery",
                schema: "support",
                table: "ticket_replies",
                sql: "delivery IN ('Text','Template','Email')");
        }
    }
}
