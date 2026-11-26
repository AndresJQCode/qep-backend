using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Shopify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "applied_payload",
                schema: "shopify",
                table: "orders",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "applied_projection",
                schema: "shopify",
                table: "orders",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "qep_attributes",
                schema: "shopify",
                table: "orders",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "order_edits",
                schema: "shopify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mirror_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    qep_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    snapshot_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    groups = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_edits", x => x.id);
                    table.CheckConstraint("CK_order_edits_status", "status IN ('Pending','Applied','Blocked','Rejected','Reviewed','Superseded')");
                    table.ForeignKey(
                        name: "FK_order_edits_orders_mirror_order_id",
                        column: x => x.mirror_order_id,
                        principalSchema: "shopify",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_edits_mirror_status",
                schema: "shopify",
                table: "order_edits",
                columns: new[] { "mirror_order_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_order_edits_tenant_status_created",
                schema: "shopify",
                table: "order_edits",
                columns: new[] { "tenant_id", "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_edits",
                schema: "shopify");

            migrationBuilder.DropColumn(
                name: "applied_payload",
                schema: "shopify",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "applied_projection",
                schema: "shopify",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "qep_attributes",
                schema: "shopify",
                table: "orders");
        }
    }
}
