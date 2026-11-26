using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Buyers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameProcessedSignupOrderId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "order_request_id",
                schema: "buyers",
                table: "processed_signups",
                newName: "order_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "order_id",
                schema: "buyers",
                table: "processed_signups",
                newName: "order_request_id");
        }
    }
}
