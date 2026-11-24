using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "price_lists",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    adjustment_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    show_prices = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true, computedColumnSql: "lower(name)", stored: true),
                    category_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    excluded_category_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    excluded_product_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_lists", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "channel_price_lists",
                schema: "catalog",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    price_list_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_price_lists", x => new { x.tenant_id, x.channel });
                    table.ForeignKey(
                        name: "FK_channel_price_lists_price_lists_price_list_id",
                        column: x => x.price_list_id,
                        principalSchema: "catalog",
                        principalTable: "price_lists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "price_list_overrides",
                schema: "catalog",
                columns: table => new
                {
                    price_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_list_overrides", x => new { x.price_list_id, x.variant_id, x.currency });
                    table.CheckConstraint("CK_price_list_overrides_amount_positive", "amount > 0");
                    table.ForeignKey(
                        name: "FK_price_list_overrides_price_lists_price_list_id",
                        column: x => x.price_list_id,
                        principalSchema: "catalog",
                        principalTable: "price_lists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_price_list_overrides_product_variants_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_channel_price_lists_price_list_id",
                schema: "catalog",
                table: "channel_price_lists",
                column: "price_list_id");

            migrationBuilder.CreateIndex(
                name: "IX_price_list_overrides_variant_id",
                schema: "catalog",
                table: "price_list_overrides",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "IX_price_lists_tenant_normalized_name",
                schema: "catalog",
                table: "price_lists",
                columns: new[] { "tenant_id", "normalized_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_price_lists",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "price_list_overrides",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "price_lists",
                schema: "catalog");
        }
    }
}
