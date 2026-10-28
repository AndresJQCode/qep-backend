using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec 2026-10-08, deploy N (expand). Prices become catalog.product_prices; the history gets a
    /// currency. Nothing is dropped: price_base_usd/cop and final_usd/cop stay, unmapped, until
    /// DropLegacyProductPriceColumns (deploy N+1, separate branch).
    ///
    /// Generated with the design-time factory and edited by hand: EF scaffolded four DropColumn
    /// calls for the legacy columns (Task 2 unmapped them), and they were removed so this step only
    /// expands. The Designer and the snapshot stay as generated: they describe a model without
    /// those columns, which is correct.
    ///
    /// The copy is checked inside the same transaction: a legacy price that did not land aborts the
    /// migration instead of deploying a catalogue with missing prices.
    /// </summary>
    public partial class AddProductPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "currency",
                schema: "catalog",
                table: "product_price_changes",
                type: "character(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "product_prices",
                schema: "catalog",
                columns: table => new
                {
                    currency = table.Column<string>(type: "character(3)", maxLength: 3, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_prices", x => new { x.product_id, x.currency });
                    table.CheckConstraint("CK_product_prices_amount_not_negative", "amount >= 0");
                    table.ForeignKey(
                        name: "FK_product_prices_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Same numeric(18,2) on both sides: the copy is exact.
            migrationBuilder.Sql(
                """
                INSERT INTO catalog.product_prices (product_id, currency, amount)
                SELECT id, 'COP', price_base_cop FROM catalog.products WHERE price_base_cop IS NOT NULL
                UNION ALL
                SELECT id, 'USD', price_base_usd FROM catalog.products WHERE price_base_usd IS NOT NULL;
                """);

            migrationBuilder.Sql(
                """
                UPDATE catalog.product_price_changes SET field = 'PriceBase', currency = 'USD' WHERE field = 'PriceBaseUsd';
                UPDATE catalog.product_price_changes SET field = 'PriceBase', currency = 'COP' WHERE field = 'PriceBaseCop';
                """);

            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    missing text;
                BEGIN
                    SELECT string_agg(p.code || ' (' || p.id || ')', ', ' ORDER BY p.code)
                    INTO missing
                    FROM catalog.products p
                    WHERE (p.price_base_cop IS NOT NULL AND NOT EXISTS (
                               SELECT 1 FROM catalog.product_prices pp
                               WHERE pp.product_id = p.id AND pp.currency = 'COP' AND pp.amount = p.price_base_cop))
                       OR (p.price_base_usd IS NOT NULL AND NOT EXISTS (
                               SELECT 1 FROM catalog.product_prices pp
                               WHERE pp.product_id = p.id AND pp.currency = 'USD' AND pp.amount = p.price_base_usd));
                    IF missing IS NOT NULL THEN
                        RAISE EXCEPTION 'AddProductPrices: legacy prices did not copy for: %', missing;
                    END IF;
                END $$;
                """);
        }

        /// <summary>
        /// A real rollback (plan decision A7): COP/USD go back into the legacy columns from the
        /// collection — deploy N may have changed them — finals are recomputed with FinalFor's
        /// rounding (PostgreSQL's numeric round is half away from zero, like
        /// MidpointRounding.AwayFromZero), and the history is renamed back. Prices and history rows
        /// in any other currency cannot be represented by the previous schema and are dropped.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE catalog.products p SET
                    price_base_cop = (SELECT pp.amount FROM catalog.product_prices pp WHERE pp.product_id = p.id AND pp.currency = 'COP'),
                    price_base_usd = (SELECT pp.amount FROM catalog.product_prices pp WHERE pp.product_id = p.id AND pp.currency = 'USD');
                UPDATE catalog.product_price_scales s SET
                    final_cop = round(p.price_base_cop * (1 - s.discount / 100), 2),
                    final_usd = round(p.price_base_usd * (1 - s.discount / 100), 2)
                FROM catalog.products p
                WHERE p.id = s.product_id;
                DELETE FROM catalog.product_price_changes WHERE field = 'PriceBase' AND currency NOT IN ('COP', 'USD');
                UPDATE catalog.product_price_changes SET field = 'PriceBaseUsd' WHERE field = 'PriceBase' AND currency = 'USD';
                UPDATE catalog.product_price_changes SET field = 'PriceBaseCop' WHERE field = 'PriceBase' AND currency = 'COP';
                """);

            migrationBuilder.DropTable(
                name: "product_prices",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "currency",
                schema: "catalog",
                table: "product_price_changes");
        }
    }
}
