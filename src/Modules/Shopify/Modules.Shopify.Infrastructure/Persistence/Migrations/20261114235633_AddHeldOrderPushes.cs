using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Shopify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHeldOrderPushes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_order_pushes_mirror_pending",
                schema: "shopify",
                table: "order_pushes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_order_pushes_status",
                schema: "shopify",
                table: "order_pushes");

            migrationBuilder.CreateIndex(
                name: "IX_order_pushes_mirror_pending",
                schema: "shopify",
                table: "order_pushes",
                column: "mirror_order_id",
                unique: true,
                filter: "status IN ('Pending','Held')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_order_pushes_status",
                schema: "shopify",
                table: "order_pushes",
                sql: "status IN ('Pending','Sent','Failed','Held')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_order_pushes_mirror_pending",
                schema: "shopify",
                table: "order_pushes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_order_pushes_status",
                schema: "shopify",
                table: "order_pushes");

            // El código anterior no conoce Held: un envío retenido vuelve a ser el Failed que ese código dejaba, con su motivo.
            migrationBuilder.Sql("UPDATE shopify.order_pushes SET status = 'Failed', version = version + 1 WHERE status = 'Held';");

            migrationBuilder.CreateIndex(
                name: "IX_order_pushes_mirror_pending",
                schema: "shopify",
                table: "order_pushes",
                column: "mirror_order_id",
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_order_pushes_status",
                schema: "shopify",
                table: "order_pushes",
                sql: "status IN ('Pending','Sent','Failed')");
        }
    }
}
