using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modules.Shopify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialShopify : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "shopify");

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "shopify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shopify_order_id = table.Column<long>(type: "bigint", nullable: false),
                    order_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    shopify_customer_id = table.Column<long>(type: "bigint", nullable: true),
                    shopify_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    shopify_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    financial_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    total_price = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    taxes_included = table.Column<bool>(type: "boolean", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    import_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    wait_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    wait_detail = table.Column<string>(type: "jsonb", nullable: true),
                    warnings = table.Column<string>(type: "jsonb", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    warnings_reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    qep_customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    qep_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    total_difference = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.id);
                    table.CheckConstraint("CK_shopify_orders_import_state", "import_state IN ('Waiting','Imported','Cancelled','Ignored')");
                });

            migrationBuilder.CreateTable(
                name: "stores",
                schema: "shopify",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    advisor_membership_id = table.Column<Guid>(type: "uuid", nullable: true),
                    billing_company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    billing_bank_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    billing_account_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    billing_currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    default_classification_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_attribute_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    document_type_attribute_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    default_identification_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_reconciled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reconcile_lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stores", x => x.connection_id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                schema: "shopify",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    webhook_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    topic = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    shop_domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    api_version = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ignored_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_deliveries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shopify_orders_connection_customer",
                schema: "shopify",
                table: "orders",
                columns: new[] { "connection_id", "shopify_customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_shopify_orders_connection_order",
                schema: "shopify",
                table: "orders",
                columns: new[] { "connection_id", "shopify_order_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shopify_orders_tenant_qep_order",
                schema: "shopify",
                table: "orders",
                columns: new[] { "tenant_id", "qep_order_id" });

            migrationBuilder.CreateIndex(
                name: "IX_shopify_orders_tenant_state_created",
                schema: "shopify",
                table: "orders",
                columns: new[] { "tenant_id", "import_state", "shopify_created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_stores_tenant",
                schema: "shopify",
                table: "stores",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_pending",
                schema: "shopify",
                table: "webhook_deliveries",
                column: "id",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_shop_domain",
                schema: "shopify",
                table: "webhook_deliveries",
                column: "shop_domain");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_webhook_id",
                schema: "shopify",
                table: "webhook_deliveries",
                column: "webhook_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "orders",
                schema: "shopify");

            migrationBuilder.DropTable(
                name: "stores",
                schema: "shopify");

            migrationBuilder.DropTable(
                name: "webhook_deliveries",
                schema: "shopify");
        }
    }
}
