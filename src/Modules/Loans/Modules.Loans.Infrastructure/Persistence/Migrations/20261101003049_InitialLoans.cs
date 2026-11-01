using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Loans.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialLoans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "loans");

            migrationBuilder.CreateTable(
                name: "loans",
                schema: "loans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    principal_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    outstanding_principal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    monthly_rate_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    disbursed_on = table.Column<DateOnly>(type: "date", nullable: false),
                    cutoff_rule = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    assignee_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_generated_period_end = table.Column<DateOnly>(type: "date", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loans", x => x.id);
                    table.CheckConstraint("CK_loans_cutoff_rule", "cutoff_rule IN ('EndOfMonth','DisbursementDay')");
                    table.CheckConstraint("CK_loans_monthly_rate", "monthly_rate_percent > 0 AND monthly_rate_percent <= 100");
                    table.CheckConstraint("CK_loans_status", "status IN ('Active','Settled')");
                    table.CheckConstraint("CK_loans_type", "type IN ('InterestOnly')");
                });

            migrationBuilder.CreateTable(
                name: "tenant_runs",
                schema: "loans",
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
                name: "capital_payments",
                schema: "loans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    paid_on = table.Column<DateOnly>(type: "date", nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    balance_after = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    void_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capital_payments", x => x.id);
                    table.CheckConstraint("CK_capital_payments_amount", "amount > 0");
                    table.ForeignKey(
                        name: "FK_capital_payments_loans_loan_id",
                        column: x => x.loan_id,
                        principalSchema: "loans",
                        principalTable: "loans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "installments",
                schema: "loans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    period_days = table.Column<int>(type: "integer", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_imported = table.Column<bool>(type: "boolean", nullable: false),
                    paid_on = table.Column<DateOnly>(type: "date", nullable: true),
                    payment_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    paid_by_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installments", x => x.id);
                    table.CheckConstraint("CK_installments_period_days", "is_imported OR period_days IS NOT NULL");
                    table.CheckConstraint("CK_installments_status", "status IN ('Pending','Paid')");
                    table.ForeignKey(
                        name: "FK_installments_loans_loan_id",
                        column: x => x.loan_id,
                        principalSchema: "loans",
                        principalTable: "loans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "installment_segments",
                schema: "loans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    start_on = table.Column<DateOnly>(type: "date", nullable: false),
                    end_on = table.Column<DateOnly>(type: "date", nullable: false),
                    principal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    days = table.Column<int>(type: "integer", nullable: false),
                    interest = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installment_segments", x => x.id);
                    table.ForeignKey(
                        name: "FK_installment_segments_installments_installment_id",
                        column: x => x.installment_id,
                        principalSchema: "loans",
                        principalTable: "installments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_capital_payments_loan_paid_on",
                schema: "loans",
                table: "capital_payments",
                columns: new[] { "loan_id", "paid_on" });

            migrationBuilder.CreateIndex(
                name: "IX_installment_segments_installment_ordinal",
                schema: "loans",
                table: "installment_segments",
                columns: new[] { "installment_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_installments_loan_period",
                schema: "loans",
                table: "installments",
                columns: new[] { "loan_id", "period_start" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_installments_tenant_status_end",
                schema: "loans",
                table: "installments",
                columns: new[] { "tenant_id", "status", "period_end" });

            migrationBuilder.CreateIndex(
                name: "IX_loans_tenant_customer",
                schema: "loans",
                table: "loans",
                columns: new[] { "tenant_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_loans_tenant_status",
                schema: "loans",
                table: "loans",
                columns: new[] { "tenant_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "capital_payments",
                schema: "loans");

            migrationBuilder.DropTable(
                name: "installment_segments",
                schema: "loans");

            migrationBuilder.DropTable(
                name: "tenant_runs",
                schema: "loans");

            migrationBuilder.DropTable(
                name: "installments",
                schema: "loans");

            migrationBuilder.DropTable(
                name: "loans",
                schema: "loans");
        }
    }
}
