using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportSurveys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "auto_close_business_days",
                schema: "support",
                table: "tenant_settings",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<bool>(
                name: "survey_enabled",
                schema: "support",
                table: "tenant_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ticket_surveys",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resolution_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rating = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    answered_via = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    rating_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    comment_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    comment_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    prompt_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    prompt_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    prompt_failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_surveys", x => x.id);
                    table.CheckConstraint("CK_ticket_surveys_answered_via", "answered_via IS NULL OR answered_via IN ('WhatsApp','Email','Portal')");
                    table.CheckConstraint("CK_ticket_surveys_channel", "channel IS NULL OR channel IN ('WhatsApp','Email','Portal')");
                    table.CheckConstraint("CK_ticket_surveys_comment", "comment IS NULL OR char_length(comment) <= 1000");
                    table.CheckConstraint("CK_ticket_surveys_rating", "rating IS NULL OR rating IN ('Good','Regular','Bad')");
                    table.CheckConstraint("CK_ticket_surveys_sent_channel", "status <> 'Sent' OR channel IS NOT NULL");
                    table.CheckConstraint("CK_ticket_surveys_status", "status IN ('Pending','Sent','Skipped','Failed')");
                    table.ForeignKey(
                        name: "FK_ticket_surveys_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "support",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_settings_auto_close",
                schema: "support",
                table: "tenant_settings",
                sql: "auto_close_business_days BETWEEN 1 AND 30");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_surveys_message",
                schema: "support",
                table: "ticket_surveys",
                column: "message_id",
                filter: "message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_surveys_pending",
                schema: "support",
                table: "ticket_surveys",
                column: "next_attempt_at",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_surveys_tenant_ticket",
                schema: "support",
                table: "ticket_surveys",
                columns: new[] { "tenant_id", "ticket_id" });

            migrationBuilder.CreateIndex(
                name: "UX_ticket_surveys_ticket_resolution",
                schema: "support",
                table: "ticket_surveys",
                columns: new[] { "ticket_id", "resolution_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ticket_surveys_token",
                schema: "support",
                table: "ticket_surveys",
                column: "token_hash",
                unique: true,
                filter: "token_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_surveys",
                schema: "support");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_settings_auto_close",
                schema: "support",
                table: "tenant_settings");

            migrationBuilder.DropColumn(
                name: "auto_close_business_days",
                schema: "support",
                table: "tenant_settings");

            migrationBuilder.DropColumn(
                name: "survey_enabled",
                schema: "support",
                table: "tenant_settings");
        }
    }
}
