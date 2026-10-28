using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec 2026-10-15 (pedidos sin cotización): el pedido pasa a tener datos propios —cabecera,
    /// cuenta de cobro, líneas, partes e historial— y <c>quotation_id</c> se vuelve opcional. Los
    /// pedidos existentes se llenan copiando su cotización; una copia a ciegas de un pedido que hoy
    /// no pudo existir (sin cotización, sin cuenta de cobro o sin líneas) se rechaza con una guarda.
    /// La migración es irreversible por diseño: el <c>Down</c> falla si ya hay pedidos sin cotización.
    /// </summary>
    public partial class AddOrderOwnData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Columnas nuevas. Las que vienen de la cotización nacen nulas y se vuelven
            //    obligatorias después de copiar; las que tienen un valor por defecto honesto, no.
            migrationBuilder.AddColumn<Guid>(name: "client_id", schema: "quotations", table: "orders", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "advisor_id", schema: "quotations", table: "orders", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<string>(name: "currency", schema: "quotations", table: "orders", type: "character varying(3)", maxLength: 3, nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "billing_company_id", schema: "quotations", table: "orders", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<string>(name: "billing_bank_name", schema: "quotations", table: "orders", type: "character varying(120)", maxLength: 120, nullable: true);
            migrationBuilder.AddColumn<string>(name: "billing_account_number", schema: "quotations", table: "orders", type: "character varying(32)", maxLength: 32, nullable: true);
            migrationBuilder.AddColumn<string>(name: "billing_account_currency", schema: "quotations", table: "orders", type: "character varying(3)", maxLength: 3, nullable: true);
            migrationBuilder.AddColumn<string>(name: "payment_method", schema: "quotations", table: "orders", type: "character varying(50)", maxLength: 50, nullable: true);
            migrationBuilder.AddColumn<bool>(name: "is_retail", schema: "quotations", table: "orders", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<int>(name: "global_scale_floor", schema: "quotations", table: "orders", type: "integer", nullable: true);
            migrationBuilder.AddColumn<bool>(name: "billing_uses_business_name", schema: "quotations", table: "orders", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(name: "is_store_pickup", schema: "quotations", table: "orders", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(name: "bills_to_final_consumer", schema: "quotations", table: "orders", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(name: "party_with_retention", schema: "quotations", table: "orders", type: "boolean", nullable: true);
            migrationBuilder.AddColumn<bool>(name: "party_vat_surplus", schema: "quotations", table: "orders", type: "boolean", nullable: true);
            migrationBuilder.AddColumn<bool>(name: "customer_with_retention", schema: "quotations", table: "orders", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(name: "customer_vat_surplus", schema: "quotations", table: "orders", type: "boolean", nullable: false, defaultValue: false);
            foreach (var column in new[] { "subtotal", "tax_amount", "discount_amount", "total", "retention_amount", "net_total" })
            {
                migrationBuilder.AddColumn<decimal>(name: column, schema: "quotations", table: "orders", type: "numeric(14,2)", precision: 14, scale: 2, nullable: false, defaultValue: 0m);
            }

            migrationBuilder.AddColumn<decimal>(name: "tax_percentage", schema: "quotations", table: "orders", type: "numeric(5,2)", precision: 5, scale: 2, nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<string>(name: "source", schema: "quotations", table: "orders", type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Quotation");
            migrationBuilder.AddColumn<string>(name: "external_reference", schema: "quotations", table: "orders", type: "character varying(64)", maxLength: 64, nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "notes",
                schema: "quotations",
                table: "orders",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "quotation_id",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            // 2. Tablas nuevas, tal como las generó EF.
            migrationBuilder.CreateTable(
                name: "order_history",
                schema: "quotations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_history", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_history_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "quotations",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_items",
                schema: "quotations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    product_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    discount_percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    discount_origin = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    tax_percentage = table.Column<int>(type: "integer", nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_items_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "quotations",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_parties",
                schema: "quotations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    identification_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    address = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    city_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_parties", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_parties_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "quotations",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_history_order_event_at",
                schema: "quotations",
                table: "order_history",
                columns: new[] { "order_id", "event_at" });

            migrationBuilder.CreateIndex(
                name: "IX_order_items_order",
                schema: "quotations",
                table: "order_items",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_parties_order_role",
                schema: "quotations",
                table: "order_parties",
                columns: new[] { "order_id", "role" },
                unique: true);

            // 3. Guardas: un pedido que hoy no pudo existir no se migra a ciegas.
            migrationBuilder.Sql("""
                DO $$
                DECLARE bad_orders text;
                BEGIN
                    SELECT string_agg(o.id::text, ', ') INTO bad_orders
                    FROM quotations.orders o
                    LEFT JOIN quotations.quotations q ON q.id = o.quotation_id
                    WHERE q.id IS NULL
                       OR q.billing_company_id IS NULL
                       OR NOT EXISTS (SELECT 1 FROM quotations.quotation_items i WHERE i.quotation_id = q.id);
                    IF bad_orders IS NOT NULL THEN
                        RAISE EXCEPTION 'AddOrderOwnData: these orders have no quotation, no billing account or no lines and must be fixed by hand first: %', bad_orders;
                    END IF;
                END $$;
                """);

            // 4. Copia de la cabecera. Las notas del pedido heredan las de la cotización, con las
            //    propias del pedido debajo (spec 2026-10-15 §4.3).
            migrationBuilder.Sql("""
                UPDATE quotations.orders o
                SET client_id = q.client_id,
                    advisor_id = q.advisor_id,
                    currency = q.currency,
                    billing_company_id = q.billing_company_id,
                    billing_bank_name = q.billing_bank_name,
                    billing_account_number = q.billing_account_number,
                    billing_account_currency = q.billing_account_currency,
                    payment_method = q.payment_method,
                    is_retail = q.is_retail,
                    global_scale_floor = q.global_scale_floor,
                    billing_uses_business_name = q.billing_uses_business_name,
                    is_store_pickup = q.is_store_pickup,
                    bills_to_final_consumer = q.bills_to_final_consumer,
                    party_with_retention = q.party_with_retention,
                    party_vat_surplus = q.party_vat_surplus,
                    customer_with_retention = q.customer_with_retention,
                    customer_vat_surplus = q.customer_vat_surplus,
                    subtotal = q.subtotal,
                    tax_percentage = q.tax_percentage,
                    tax_amount = q.tax_amount,
                    discount_amount = q.discount_amount,
                    total = q.total,
                    retention_amount = q.retention_amount,
                    net_total = q.net_total,
                    notes = CASE
                        WHEN q.notes IS NULL OR btrim(q.notes) = '' THEN o.notes
                        WHEN o.notes IS NULL OR btrim(o.notes) = '' THEN q.notes
                        ELSE q.notes || E'\n\n' || o.notes END,
                    source = 'Quotation'
                FROM quotations.quotations q
                WHERE q.id = o.quotation_id;
                """);

            // 5. Copia de líneas, partes y el evento inicial del historial.
            migrationBuilder.Sql("""
                INSERT INTO quotations.order_items (
                    id, order_id, product_id, product_code, product_name, quantity, unit_price, discount_origin,
                    discount_percentage, discount_amount, subtotal, tax_percentage, tax_amount, position, created_at, updated_at)
                SELECT gen_random_uuid(), o.id, i.product_id, i.product_code, i.product_name, i.quantity, i.unit_price,
                       i.discount_origin, i.discount_percentage, i.discount_amount, i.subtotal, i.tax_percentage, i.tax_amount,
                       i.position, i.created_at, i.updated_at
                FROM quotations.orders o
                JOIN quotations.quotation_items i ON i.quotation_id = o.quotation_id;

                INSERT INTO quotations.order_parties (
                    id, order_id, role, name, identification_number, phone, email, address, department_id, city_id)
                SELECT gen_random_uuid(), o.id, p.role, p.name, p.identification_number, p.phone, p.email, p.address,
                       p.department_id, p.city_id
                FROM quotations.orders o
                JOIN quotations.quotation_parties p ON p.quotation_id = o.quotation_id;

                INSERT INTO quotations.order_history (id, order_id, event_type, event_at, member_id, details, created_at)
                SELECT gen_random_uuid(), o.id, 'Created', o.converted_at, o.converted_by,
                       'Creado desde la cotización ' || q.quotation_number || '.', o.converted_at
                FROM quotations.orders o
                JOIN quotations.quotations q ON q.id = o.quotation_id;

                UPDATE quotations.quotations q
                SET status = 'Converted'
                WHERE status <> 'Converted' AND EXISTS (SELECT 1 FROM quotations.orders o WHERE o.quotation_id = q.id);
                """);

            // 6. Ahora sí, obligatorias.
            migrationBuilder.AlterColumn<Guid>(name: "client_id", schema: "quotations", table: "orders", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "advisor_id", schema: "quotations", table: "orders", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "currency", schema: "quotations", table: "orders", type: "character varying(3)", maxLength: 3, nullable: false, oldClrType: typeof(string), oldType: "character varying(3)", oldMaxLength: 3, oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "billing_company_id", schema: "quotations", table: "orders", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "billing_bank_name", schema: "quotations", table: "orders", type: "character varying(120)", maxLength: 120, nullable: false, oldClrType: typeof(string), oldType: "character varying(120)", oldMaxLength: 120, oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "billing_account_number", schema: "quotations", table: "orders", type: "character varying(32)", maxLength: 32, nullable: false, oldClrType: typeof(string), oldType: "character varying(32)", oldMaxLength: 32, oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "billing_account_currency", schema: "quotations", table: "orders", type: "character varying(3)", maxLength: 3, nullable: false, oldClrType: typeof(string), oldType: "character varying(3)", oldMaxLength: 3, oldNullable: true);

            // 7. Índices y check: el único de quotation_id pasa a parcial para dejar entrar pedidos sin
            //    cotización.
            migrationBuilder.DropIndex(
                name: "IX_orders_quotation",
                schema: "quotations",
                table: "orders");

            migrationBuilder.CreateIndex(
                name: "IX_orders_quotation",
                schema: "quotations",
                table: "orders",
                column: "quotation_id",
                unique: true,
                filter: "quotation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_orders_tenant_advisor",
                schema: "quotations",
                table: "orders",
                columns: new[] { "tenant_id", "advisor_id" });

            migrationBuilder.CreateIndex(
                name: "IX_orders_tenant_client",
                schema: "quotations",
                table: "orders",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "IX_orders_tenant_source",
                schema: "quotations",
                table: "orders",
                columns: new[] { "tenant_id", "source" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_orders_source",
                schema: "quotations",
                table: "orders",
                sql: "source IN ('Quotation','Manual','Shopify')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_history",
                schema: "quotations");

            migrationBuilder.DropTable(
                name: "order_items",
                schema: "quotations");

            migrationBuilder.DropTable(
                name: "order_parties",
                schema: "quotations");

            migrationBuilder.DropIndex(
                name: "IX_orders_quotation",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_orders_tenant_advisor",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_orders_tenant_client",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_orders_tenant_source",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_orders_source",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "advisor_id",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "billing_account_currency",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "billing_account_number",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "billing_bank_name",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "billing_company_id",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "billing_uses_business_name",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "bills_to_final_consumer",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "client_id",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "currency",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "customer_vat_surplus",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "customer_with_retention",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "discount_amount",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "external_reference",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "global_scale_floor",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "is_retail",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "is_store_pickup",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "net_total",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "party_vat_surplus",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "party_with_retention",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "payment_method",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "retention_amount",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "source",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "subtotal",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "tax_amount",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "tax_percentage",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "total",
                schema: "quotations",
                table: "orders");

            // Falla si hay pedidos sin cotización: la migración es irreversible por diseño.
            migrationBuilder.AlterColumn<Guid>(
                name: "quotation_id",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "notes",
                schema: "quotations",
                table: "orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_quotation",
                schema: "quotations",
                table: "orders",
                column: "quotation_id",
                unique: true);
        }
    }
}
