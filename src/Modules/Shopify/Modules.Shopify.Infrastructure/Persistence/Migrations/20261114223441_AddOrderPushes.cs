using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Shopify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderPushes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_pushes",
                schema: "shopify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mirror_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    qep_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    groups = table.Column<string[]>(type: "text[]", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_pushes", x => x.id);
                    table.CheckConstraint("CK_order_pushes_status", "status IN ('Pending','Sent','Failed')");
                    table.ForeignKey(
                        name: "FK_order_pushes_orders_mirror_order_id",
                        column: x => x.mirror_order_id,
                        principalSchema: "shopify",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_pushes_mirror_created",
                schema: "shopify",
                table: "order_pushes",
                columns: new[] { "mirror_order_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_order_pushes_mirror_pending",
                schema: "shopify",
                table: "order_pushes",
                column: "mirror_order_id",
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_order_pushes_status_lease",
                schema: "shopify",
                table: "order_pushes",
                columns: new[] { "status", "lease_until" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_pushes",
                schema: "shopify");
        }
    }
}
