using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Pipeline.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pipeline");

            migrationBuilder.CreateTable(
                name: "deal_quotations",
                schema: "pipeline",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quotation_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    total = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    order_cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deal_quotations", x => new { x.tenant_id, x.quotation_id });
                    table.CheckConstraint("CK_deal_quotations_status", "status IN ('Sent','Converted')");
                });

            migrationBuilder.CreateTable(
                name: "deal_stage_history",
                schema: "pipeline",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    from_stage_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    actor_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    probability = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deal_stage_history", x => x.id);
                    table.CheckConstraint("CK_deal_stage_history_actor_kind", "actor_kind IN ('User','System')");
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "pipeline",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claimed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_messages", x => new { x.consumer, x.message_id });
                });

            migrationBuilder.CreateTable(
                name: "pipelines",
                schema: "pipeline",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pipelines", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "reasons",
                schema: "pipeline",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reasons", x => x.id);
                    table.CheckConstraint("CK_reasons_kind", "kind IN ('Loss','Dismiss')");
                });

            migrationBuilder.CreateTable(
                name: "triage_decisions",
                schema: "pipeline",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    deal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dismiss_reason_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_against_inbound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    actor_member_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_triage_decisions", x => x.id);
                    table.CheckConstraint("CK_triage_decisions_actor_kind", "actor_kind IN ('User','System')");
                    table.CheckConstraint("CK_triage_decisions_kind", "kind IN ('Created','Dismissed')");
                });

            migrationBuilder.CreateTable(
                name: "stages",
                schema: "pipeline",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    probability = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    stall_days = table.Column<int>(type: "integer", nullable: true),
                    is_quotation_stage = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stages", x => x.id);
                    table.CheckConstraint("CK_stages_kind", "kind IN ('Open','Won','Lost')");
                    table.CheckConstraint("CK_stages_probability", "probability BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "FK_stages_pipelines_pipeline_id",
                        column: x => x.pipeline_id,
                        principalSchema: "pipeline",
                        principalTable: "pipelines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deals",
                schema: "pipeline",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    expected_close_date = table.Column<DateOnly>(type: "date", nullable: true),
                    stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_entered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    primary_quotation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    origin_conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    loss_reason_id = table.Column<Guid>(type: "uuid", nullable: true),
                    loss_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    history_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deals", x => x.id);
                    table.CheckConstraint("CK_deals_amount", "amount IS NULL OR amount >= 0");
                    table.CheckConstraint("CK_deals_source", "source IN ('Manual','Conversation','Quotation')");
                    table.ForeignKey(
                        name: "FK_deals_stages_stage_id",
                        column: x => x.stage_id,
                        principalSchema: "pipeline",
                        principalTable: "stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deal_quotations_customer",
                schema: "pipeline",
                table: "deal_quotations",
                columns: new[] { "tenant_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_deal_quotations_deal",
                schema: "pipeline",
                table: "deal_quotations",
                columns: new[] { "tenant_id", "deal_id" });

            migrationBuilder.CreateIndex(
                name: "IX_deal_stage_history_deal",
                schema: "pipeline",
                table: "deal_stage_history",
                columns: new[] { "tenant_id", "deal_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_deal_stage_history_deal_ordinal",
                schema: "pipeline",
                table: "deal_stage_history",
                columns: new[] { "tenant_id", "deal_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_deals_stage_id",
                schema: "pipeline",
                table: "deals",
                column: "stage_id");

            migrationBuilder.CreateIndex(
                name: "IX_deals_tenant_customer_closed",
                schema: "pipeline",
                table: "deals",
                columns: new[] { "tenant_id", "customer_id", "closed_at" },
                filter: "closed_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_deals_tenant_customer_open",
                schema: "pipeline",
                table: "deals",
                columns: new[] { "tenant_id", "customer_id" },
                filter: "closed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_deals_tenant_stage",
                schema: "pipeline",
                table: "deals",
                columns: new[] { "tenant_id", "stage_id", "stage_entered_at" });

            migrationBuilder.CreateIndex(
                name: "IX_pipelines_tenant",
                schema: "pipeline",
                table: "pipelines",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reasons_tenant_kind",
                schema: "pipeline",
                table: "reasons",
                columns: new[] { "tenant_id", "kind", "position" });

            migrationBuilder.CreateIndex(
                name: "IX_stages_pipeline_position",
                schema: "pipeline",
                table: "stages",
                columns: new[] { "pipeline_id", "position" });

            migrationBuilder.CreateIndex(
                name: "IX_triage_decisions_conversation_inbound",
                schema: "pipeline",
                table: "triage_decisions",
                columns: new[] { "tenant_id", "conversation_id", "decided_against_inbound_at" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deal_quotations",
                schema: "pipeline");

            migrationBuilder.DropTable(
                name: "deal_stage_history",
                schema: "pipeline");

            migrationBuilder.DropTable(
                name: "deals",
                schema: "pipeline");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "pipeline");

            migrationBuilder.DropTable(
                name: "reasons",
                schema: "pipeline");

            migrationBuilder.DropTable(
                name: "triage_decisions",
                schema: "pipeline");

            migrationBuilder.DropTable(
                name: "stages",
                schema: "pipeline");

            migrationBuilder.DropTable(
                name: "pipelines",
                schema: "pipeline");
        }
    }
}
