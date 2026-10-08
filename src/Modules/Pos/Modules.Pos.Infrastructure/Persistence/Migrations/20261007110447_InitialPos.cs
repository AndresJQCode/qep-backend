using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pos");

            migrationBuilder.CreateTable(
                name: "cash_sessions",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cashier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cashier_name = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    company_tax_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    company_address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    company_phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    opening_float = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sales_count = table.Column<int>(type: "integer", nullable: false),
                    voided_count = table.Column<int>(type: "integer", nullable: false),
                    sales_total = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    cash_total = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    card_total = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    transfer_total = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    expected_cash = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    counted_cash = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    cash_difference = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    closing_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cash_sessions", x => x.id);
                    table.CheckConstraint("CK_cash_sessions_opening_float", "opening_float >= 0");
                    table.CheckConstraint("CK_cash_sessions_status", "status IN ('Open','Closed')");
                });

            migrationBuilder.CreateTable(
                name: "sale_number_counters",
                schema: "pos",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_number_counters", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "sales",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cash_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cashier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    customer_identification_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    customer_identification_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    change_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    void_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    voided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales", x => x.id);
                    table.CheckConstraint("CK_sales_status", "status IN ('Completed','Voided')");
                    table.ForeignKey(
                        name: "FK_sales_cash_sessions_cash_session_id",
                        column: x => x.cash_session_id,
                        principalSchema: "pos",
                        principalTable: "cash_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sale_lines",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    product_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    discount_percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    tax_percentage = table.Column<int>(type: "integer", nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_sale_lines_sales_sale_id",
                        column: x => x.sale_id,
                        principalSchema: "pos",
                        principalTable: "sales",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sale_payments",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    tendered = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    reference = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_payments", x => x.id);
                    table.CheckConstraint("CK_sale_payments_method", "method IN ('Cash','Card','Transfer')");
                    table.ForeignKey(
                        name: "FK_sale_payments_sales_sale_id",
                        column: x => x.sale_id,
                        principalSchema: "pos",
                        principalTable: "sales",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cash_sessions_company",
                schema: "pos",
                table: "cash_sessions",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_cash_sessions_one_open_per_cashier",
                schema: "pos",
                table: "cash_sessions",
                columns: new[] { "tenant_id", "cashier_id" },
                unique: true,
                filter: "status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_cash_sessions_tenant_opened",
                schema: "pos",
                table: "cash_sessions",
                columns: new[] { "tenant_id", "opened_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_sale_lines_sale_id_position",
                schema: "pos",
                table: "sale_lines",
                columns: new[] { "sale_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_payments_sale_id_position",
                schema: "pos",
                table: "sale_payments",
                columns: new[] { "sale_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_session",
                schema: "pos",
                table: "sales",
                columns: new[] { "cash_session_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_tenant_created",
                schema: "pos",
                table: "sales",
                columns: new[] { "tenant_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_sales_tenant_number",
                schema: "pos",
                table: "sales",
                columns: new[] { "tenant_id", "sale_number" },
                unique: true);

            // FK real a la empresa emisora (spec, decisión 8): borrar una empresa con cajas sale
            // como 422 companies.company.in_use: CompaniesUnitOfWork traduce el SQLSTATE 23001
            // (restrict_violation, el de ON DELETE RESTRICT) igual que el 23503. A mano porque EF no modela relaciones entre DbContext de
            // módulos distintos (mismo precedente que companies → geography).
            migrationBuilder.AddForeignKey(
                name: "FK_cash_sessions_companies_company_id",
                schema: "pos",
                table: "cash_sessions",
                column: "company_id",
                principalSchema: "companies",
                principalTable: "companies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_cash_sessions_companies_company_id",
                schema: "pos",
                table: "cash_sessions");

            migrationBuilder.DropTable(
                name: "sale_lines",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "sale_number_counters",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "sale_payments",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "sales",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "cash_sessions",
                schema: "pos");
        }
    }
}
