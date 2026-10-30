using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHistorySync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "history_managed",
                schema: "messaging",
                table: "conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "history_syncs",
                schema: "messaging",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    contacts_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    contacts_request_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    contacts_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    contacts_error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    history_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    history_request_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    history_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    history_error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    history_phase = table.Column<short>(type: "smallint", nullable: true),
                    history_progress = table.Column<short>(type: "smallint", nullable: true),
                    declined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    claimed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_history_syncs", x => x.connection_id);
                    table.CheckConstraint("CK_history_syncs_contacts_state", "contacts_state IN ('Pending','Requested','Failed')");
                    table.CheckConstraint("CK_history_syncs_history_state", "history_state IN ('Pending','Requested','Failed')");
                    table.CheckConstraint("CK_history_syncs_progress", "history_progress BETWEEN 0 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "messaging",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claimed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_messages", x => new { x.consumer, x.message_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_history_syncs_pending",
                schema: "messaging",
                table: "history_syncs",
                column: "claimed_until",
                filter: "contacts_state = 'Pending' OR history_state = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_history_syncs_tenant",
                schema: "messaging",
                table: "history_syncs",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "history_syncs",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "messaging");

            migrationBuilder.DropColumn(
                name: "history_managed",
                schema: "messaging",
                table: "conversations");
        }
    }
}
