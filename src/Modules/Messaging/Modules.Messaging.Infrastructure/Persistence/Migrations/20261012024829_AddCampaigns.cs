using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "campaigns",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    template_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    template_language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    template_category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    audience_tag_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    audience_customer_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    audience_frozen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaigns", x => x.id);
                    table.CheckConstraint("CK_campaigns_audience", "cardinality(audience_tag_ids) + cardinality(audience_customer_ids) > 0");
                    table.CheckConstraint("CK_campaigns_customers_cap", "cardinality(audience_customer_ids) <= 500");
                    table.CheckConstraint("CK_campaigns_status", "status IN ('Draft','Scheduled','Sending','Completed','Cancelled','Failed')");
                });

            migrationBuilder.CreateTable(
                name: "campaign_recipients",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    wa_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    user_id = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaign_recipients", x => x.id);
                    table.CheckConstraint("CK_campaign_recipients_status", "status IN ('Pending','Sending','Sent','Failed','Skipped')");
                    table.ForeignKey(
                        name: "FK_campaign_recipients_campaign",
                        column: x => x.campaign_id,
                        principalSchema: "messaging",
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_campaign_recipients_campaign_customer",
                schema: "messaging",
                table: "campaign_recipients",
                columns: new[] { "campaign_id", "customer_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_campaign_recipients_campaign_status",
                schema: "messaging",
                table: "campaign_recipients",
                columns: new[] { "campaign_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_campaign_recipients_due",
                schema: "messaging",
                table: "campaign_recipients",
                column: "next_attempt_at",
                filter: "status IN ('Pending','Sending')");

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_scheduled",
                schema: "messaging",
                table: "campaigns",
                column: "scheduled_at",
                filter: "status = 'Scheduled'");

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_sending",
                schema: "messaging",
                table: "campaigns",
                column: "status",
                filter: "status = 'Sending'");

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_tenant_created",
                schema: "messaging",
                table: "campaigns",
                columns: new[] { "tenant_id", "created_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "campaign_recipients",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "campaigns",
                schema: "messaging");
        }
    }
}
