using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Store.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "store");

            migrationBuilder.CreateTable(
                name: "order_requests",
                schema: "store",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    client_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    pricing_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    consent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consent_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    consent_policy_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    consent_policy_set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    converted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    converted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    order_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    order_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    total_difference = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    discarded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    discarded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    discard_reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    discard_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    has_shipping = table.Column<bool>(type: "boolean", nullable: false),
                    billing_address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    billing_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    billing_identification_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    billing_identification_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    billing_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    billing_phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    billing_phone_e164 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    billing_city_id = table.Column<Guid>(type: "uuid", nullable: true),
                    billing_city_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    billing_country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    billing_department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    billing_department_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    shipping_address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    shipping_phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    shipping_phone_e164 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    shipping_recipient_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    shipping_city_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shipping_city_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    shipping_country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    shipping_department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shipping_department_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_requests", x => x.id);
                    table.CheckConstraint("CK_order_requests_billing_lane", "(billing_country = 'CO') = (billing_department_id IS NOT NULL AND billing_city_id IS NOT NULL)");
                    table.CheckConstraint("CK_order_requests_discard_reason", "discard_reason IS NULL OR discard_reason IN ('Spam','Duplicate','Other')");
                    table.CheckConstraint("CK_order_requests_shipping", "has_shipping = (shipping_phone IS NOT NULL AND shipping_phone_e164 IS NOT NULL AND shipping_address IS NOT NULL AND shipping_country IS NOT NULL AND shipping_city_name IS NOT NULL)");
                    table.CheckConstraint("CK_order_requests_shipping_lane", "shipping_country IS NULL OR ((shipping_country = 'CO') = (shipping_department_id IS NOT NULL AND shipping_city_id IS NOT NULL))");
                    table.CheckConstraint("CK_order_requests_status", "status IN ('New','Converted','Discarded')");
                });

            migrationBuilder.CreateTable(
                name: "request_counters",
                schema: "store",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_counters", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "store_domains",
                schema: "store",
                columns: table => new
                {
                    hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_domains", x => x.hostname);
                });

            migrationBuilder.CreateTable(
                name: "stores",
                schema: "store",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    is_published = table.Column<bool>(type: "boolean", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    primary_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    on_primary_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    accent_text_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    banner_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    banner_text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    whatsapp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    schedule = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    instagram_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    facebook_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    tiktok_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    data_policy_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    data_policy_set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pricing_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    primary_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    sales_countries = table.Column<string[]>(type: "character varying(2)[]", nullable: false, defaultValueSql: "'{}'"),
                    featured_category_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'"),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stores", x => x.tenant_id);
                    table.CheckConstraint("CK_stores_colors", "(primary_color IS NULL OR primary_color ~ '^#[0-9A-F]{6}$') AND (on_primary_color IS NULL OR on_primary_color ~ '^#[0-9A-F]{6}$') AND (accent_text_color IS NULL OR accent_text_color ~ '^#[0-9A-F]{6}$')");
                    table.CheckConstraint("CK_stores_pricing_mode", "pricing_mode IN ('RetailOnly','QuantityScales')");
                });

            migrationBuilder.CreateTable(
                name: "order_request_lines",
                schema: "store",
                columns: table => new
                {
                    position = table.Column<int>(type: "integer", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    discount_percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    discount_origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    discounted_unit_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_request_lines", x => new { x.request_id, x.position });
                    table.ForeignKey(
                        name: "FK_order_request_lines_order_requests_request_id",
                        column: x => x.request_id,
                        principalSchema: "store",
                        principalTable: "order_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "store_currencies",
                schema: "store",
                columns: table => new
                {
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_bank_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    billing_account_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    billing_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_currencies", x => new { x.tenant_id, x.currency });
                    table.CheckConstraint("CK_store_currencies_account_currency", "billing_currency = currency");
                    table.ForeignKey(
                        name: "FK_store_currencies_stores_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "store",
                        principalTable: "stores",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_requests_order",
                schema: "store",
                table: "order_requests",
                column: "order_id",
                unique: true,
                filter: "order_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_order_requests_tenant_client_request",
                schema: "store",
                table: "order_requests",
                columns: new[] { "tenant_id", "client_request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_requests_tenant_converted_by",
                schema: "store",
                table: "order_requests",
                columns: new[] { "tenant_id", "converted_by" });

            // A mano: el modelo no puede declarar un índice que cruce tenant_id con una columna de un complex type (Billing).
            migrationBuilder.CreateIndex(
                name: "IX_order_requests_tenant_billing_phone",
                schema: "store",
                table: "order_requests",
                columns: new[] { "tenant_id", "billing_phone_e164" });

            migrationBuilder.CreateIndex(
                name: "IX_order_requests_tenant_number",
                schema: "store",
                table: "order_requests",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_requests_tenant_status_received",
                schema: "store",
                table: "order_requests",
                columns: new[] { "tenant_id", "status", "received_at" });

            migrationBuilder.CreateIndex(
                name: "IX_store_domains_tenant",
                schema: "store",
                table: "store_domains",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_stores_slug",
                schema: "store",
                table: "stores",
                column: "slug",
                unique: true,
                filter: "slug IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_request_lines",
                schema: "store");

            migrationBuilder.DropTable(
                name: "request_counters",
                schema: "store");

            migrationBuilder.DropTable(
                name: "store_currencies",
                schema: "store");

            migrationBuilder.DropTable(
                name: "store_domains",
                schema: "store");

            migrationBuilder.DropTable(
                name: "order_requests",
                schema: "store");

            migrationBuilder.DropTable(
                name: "stores",
                schema: "store");
        }
    }
}
