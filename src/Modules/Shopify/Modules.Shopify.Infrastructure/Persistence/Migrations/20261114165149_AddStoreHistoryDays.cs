using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Shopify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreHistoryDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "history_days",
                schema: "shopify",
                table: "stores",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_stores_history_days",
                schema: "shopify",
                table: "stores",
                sql: "history_days IS NULL OR history_days BETWEEN 1 AND 30");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_stores_history_days",
                schema: "shopify",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "history_days",
                schema: "shopify",
                table: "stores");
        }
    }
}
