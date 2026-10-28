using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuotationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_quotation_settings",
                schema: "quotations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    minimum_units = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_quotation_settings", x => x.tenant_id);
                    table.CheckConstraint("CK_tenant_quotation_settings_minimum_units", "minimum_units >= 1");
                });

            migrationBuilder.CreateTable(
                name: "tenant_minimum_totals",
                schema: "quotations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character(3)", maxLength: 3, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_minimum_totals", x => new { x.tenant_id, x.currency });
                    table.CheckConstraint("CK_tenant_minimum_totals_amount_not_negative", "amount >= 0");
                    table.ForeignKey(
                        name: "FK_tenant_minimum_totals_tenant_quotation_settings_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "quotations",
                        principalTable: "tenant_quotation_settings",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Today's constants for every existing tenant, so the gate behaves exactly as before this
            // deploy (spec). Read once from tenancy.tenants — Tenancy migrates before Quotations at
            // startup (Program.cs). The guard keeps the migration runnable on a database that only
            // has Quotations. A tenant registered later has no row and reads QuotationSettings.Default,
            // the same values.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('tenancy.tenants') IS NOT NULL THEN
                        INSERT INTO quotations.tenant_quotation_settings (tenant_id, minimum_units)
                        SELECT id, 6 FROM tenancy.tenants
                        ON CONFLICT (tenant_id) DO NOTHING;

                        INSERT INTO quotations.tenant_minimum_totals (tenant_id, currency, amount)
                        SELECT id, 'COP', 500000 FROM tenancy.tenants
                        UNION ALL
                        SELECT id, 'USD', 200 FROM tenancy.tenants
                        ON CONFLICT (tenant_id, currency) DO NOTHING;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_minimum_totals",
                schema: "quotations");

            migrationBuilder.DropTable(
                name: "tenant_quotation_settings",
                schema: "quotations");
        }
    }
}
