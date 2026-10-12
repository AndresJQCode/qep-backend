using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowButtonMarketingConsentSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_customers_marketing_opt_out_source",
                schema: "customers",
                table: "customers");

            migrationBuilder.AddCheckConstraint(
                name: "CK_customers_marketing_opt_out_source",
                schema: "customers",
                table: "customers",
                sql: "marketing_opt_out_source IS NULL OR marketing_opt_out_source IN ('manual', 'meta', 'button')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_customers_marketing_opt_out_source",
                schema: "customers",
                table: "customers");

            migrationBuilder.AddCheckConstraint(
                name: "CK_customers_marketing_opt_out_source",
                schema: "customers",
                table: "customers",
                sql: "marketing_opt_out_source IS NULL OR marketing_opt_out_source IN ('manual', 'meta')");
        }
    }
}
