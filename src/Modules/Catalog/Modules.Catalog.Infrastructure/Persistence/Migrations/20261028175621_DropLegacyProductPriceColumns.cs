using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec 2026-10-08 (multi-currency), deploy N+1 (contract). Drops the legacy price columns only
    /// after proving in the same transaction that catalog.product_prices holds what they held. Any
    /// drift is a RAISE EXCEPTION that aborts everything and names the rows to fix. Tolerances and
    /// the removed-through-the-API exception are plan decisions A8 and A9.
    ///
    /// Written against the variant schema (spec 2026-10-20): the legacy product-level price is the
    /// price of the default variant, which carries the product's id (AddProductVariants,
    /// Product.Create), so product_prices and the PriceBase history are read by
    /// <c>variant_id = product id</c>. Other variants never had legacy prices and do not count. A
    /// scale final is checked against the price of the scale's own variant.
    ///
    /// Generated with the design-time factory: EF scaffolded an empty Up/Down because the model has
    /// not mapped these columns since AddProductPrices, so the SQL is hand-written and the snapshot
    /// does not change.
    /// </summary>
    public partial class DropLegacyProductPriceColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A legacy price is covered by its default variant's product_prices row, or skipped when
            // the latest PriceBase history row of that variant and currency removed it through the
            // API (A8: the legacy column is stale, not drift; ties in the same instant go to the
            // higher id). The inner join on product_variants skips a product whose default variant
            // was deleted through the API: AddProductVariants created one for every product, and
            // deleting it cascades its prices and its history, so there is no A8 row left to read
            // and nothing left to lose. Scale finals must equal FinalFor(base, discount) within the
            // one-cent tolerance the legacy validator allowed (A9); scales written after deploy N
            // have null finals and are not checked.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    drift text;
                BEGIN
                    SELECT string_agg(v.code || ' (' || p.id || ', ' || legacy.currency || ')', ', '
                                      ORDER BY v.code, legacy.currency)
                    INTO drift
                    FROM catalog.products p
                    JOIN catalog.product_variants v ON v.id = p.id
                    CROSS JOIN LATERAL (VALUES ('COP', p.price_base_cop), ('USD', p.price_base_usd)) AS legacy(currency, amount)
                    WHERE legacy.amount IS NOT NULL
                      AND NOT EXISTS (
                          SELECT 1 FROM catalog.product_prices pp
                          WHERE pp.variant_id = p.id AND pp.currency = legacy.currency)
                      AND NOT EXISTS (
                          SELECT 1 FROM (
                              SELECT c.new_value
                              FROM catalog.product_price_changes c
                              WHERE c.variant_id = p.id AND c.field = 'PriceBase' AND c.currency = legacy.currency
                              ORDER BY c.changed_at DESC, c.id DESC
                              LIMIT 1) AS latest
                          WHERE latest.new_value IS NULL);
                    IF drift IS NOT NULL THEN
                        RAISE EXCEPTION 'DropLegacyProductPriceColumns aborted: legacy base price without a product_prices row for %', drift;
                    END IF;

                    SELECT string_agg(v.code || ' scale ' || s.id || ' (' || legacy.currency || ')', ', '
                                      ORDER BY v.code, s.id, legacy.currency)
                    INTO drift
                    FROM catalog.product_price_scales s
                    JOIN catalog.product_variants v ON v.id = s.variant_id
                    CROSS JOIN LATERAL (VALUES ('COP', s.final_cop), ('USD', s.final_usd)) AS legacy(currency, final)
                    LEFT JOIN catalog.product_prices pp
                           ON pp.variant_id = s.variant_id AND pp.currency = legacy.currency
                    WHERE legacy.final IS NOT NULL
                      AND (pp.amount IS NULL
                           OR abs(legacy.final - round(pp.amount * (1 - s.discount / 100), 2)) > 0.01);
                    IF drift IS NOT NULL THEN
                        RAISE EXCEPTION 'DropLegacyProductPriceColumns aborted: scale finals that do not match FinalFor(base, discount): %', drift;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE catalog.products DROP COLUMN price_base_cop, DROP COLUMN price_base_usd;
                ALTER TABLE catalog.product_price_scales DROP COLUMN final_cop, DROP COLUMN final_usd;
                """);
        }

        /// <summary>
        /// Puts the columns back, because the Down of AddProductVariants and AddProductPrices still
        /// expect them: the product price from its default variant (null when that variant was
        /// deleted), every scale final recomputed as FinalFor from its own variant's price.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE catalog.products ADD COLUMN price_base_cop numeric(18,2) NULL, ADD COLUMN price_base_usd numeric(18,2) NULL;
                ALTER TABLE catalog.product_price_scales ADD COLUMN final_cop numeric(18,2) NULL, ADD COLUMN final_usd numeric(18,2) NULL;
                UPDATE catalog.products p SET
                    price_base_cop = (SELECT pp.amount FROM catalog.product_prices pp WHERE pp.variant_id = p.id AND pp.currency = 'COP'),
                    price_base_usd = (SELECT pp.amount FROM catalog.product_prices pp WHERE pp.variant_id = p.id AND pp.currency = 'USD');
                UPDATE catalog.product_price_scales s SET
                    final_cop = round((SELECT pp.amount FROM catalog.product_prices pp WHERE pp.variant_id = s.variant_id AND pp.currency = 'COP') * (1 - s.discount / 100), 2),
                    final_usd = round((SELECT pp.amount FROM catalog.product_prices pp WHERE pp.variant_id = s.variant_id AND pp.currency = 'USD') * (1 - s.discount / 100), 2);
                """);
        }
    }
}
