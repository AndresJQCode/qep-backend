using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.ServiceContracts.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "billing");

            migrationBuilder.CreateTable(
                name: "charges",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    paid_on = table.Column<DateOnly>(type: "date", nullable: true),
                    payment_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    paid_by_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_charges", x => x.id);
                    table.CheckConstraint("CK_charges_status", "status IN ('Pending','Paid','Cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "customer_messages",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_date = table.Column<DateOnly>(type: "date", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    claimed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_messages", x => x.id);
                    table.CheckConstraint("CK_customer_messages_kind", "kind IN ('Reminder','Overdue')");
                    table.CheckConstraint("CK_customer_messages_status", "status IN ('Sending','Sent','Failed')");
                });

            migrationBuilder.CreateTable(
                name: "customer_profiles",
                schema: "billing",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    assignee_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_profiles", x => new { x.tenant_id, x.customer_id });
                    table.CheckConstraint("CK_customer_profiles_channel", "channel IN ('WhatsApp','Email')");
                });

            migrationBuilder.CreateTable(
                name: "daily_digests",
                schema: "billing",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    claimed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_digests", x => new { x.tenant_id, x.member_id, x.run_date });
                    table.CheckConstraint("CK_daily_digests_status", "status IN ('Sending','Sent','Failed')");
                });

            migrationBuilder.CreateTable(
                name: "services",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    cycle_months = table.Column<short>(type: "smallint", nullable: false),
                    first_due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reactivated_on = table.Column<DateOnly>(type: "date", nullable: true),
                    last_generated_due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_services", x => x.id);
                    table.CheckConstraint("CK_services_cycle_months", "cycle_months IN (1, 6, 12)");
                    table.CheckConstraint("CK_services_status", "status IN ('Active','Paused','Cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "settings",
                schema: "billing",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    whatsapp_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_runs",
                schema: "billing",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_date = table.Column<DateOnly>(type: "date", nullable: false),
                    claimed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_runs", x => new { x.tenant_id, x.run_date });
                });

            migrationBuilder.CreateTable(
                name: "reminder_steps",
                schema: "billing",
                columns: table => new
                {
                    charge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminder_steps", x => new { x.charge_id, x.step });
                    table.ForeignKey(
                        name: "FK_reminder_steps_customer_messages_message_id",
                        column: x => x.message_id,
                        principalSchema: "billing",
                        principalTable: "customer_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_charges_service_due",
                schema: "billing",
                table: "charges",
                columns: new[] { "service_id", "due_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_charges_tenant_customer_status",
                schema: "billing",
                table: "charges",
                columns: new[] { "tenant_id", "customer_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_charges_tenant_status_due",
                schema: "billing",
                table: "charges",
                columns: new[] { "tenant_id", "status", "due_date" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_messages_tenant_customer_day",
                schema: "billing",
                table: "customer_messages",
                columns: new[] { "tenant_id", "customer_id", "run_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reminder_steps_message",
                schema: "billing",
                table: "reminder_steps",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "IX_services_tenant_customer",
                schema: "billing",
                table: "services",
                columns: new[] { "tenant_id", "customer_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "charges",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "customer_profiles",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "daily_digests",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "reminder_steps",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "services",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "settings",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "tenant_runs",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "customer_messages",
                schema: "billing");
        }
    }
}
