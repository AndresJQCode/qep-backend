using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreOrderSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_orders_source",
                schema: "quotations",
                table: "orders");

            migrationBuilder.AddCheckConstraint(
                name: "CK_orders_source",
                schema: "quotations",
                table: "orders",
                sql: "source IN ('Quotation','Manual','Shopify','Store')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM quotations.orders WHERE source = 'Store') THEN
                        RAISE EXCEPTION 'Cannot revert AddStoreOrderSource: quotations.orders has orders from the store.';
                    END IF;
                END $$;");

            migrationBuilder.DropCheckConstraint(
                name: "CK_orders_source",
                schema: "quotations",
                table: "orders");

            migrationBuilder.AddCheckConstraint(
                name: "CK_orders_source",
                schema: "quotations",
                table: "orders",
                sql: "source IN ('Quotation','Manual','Shopify')");
        }
    }
}
