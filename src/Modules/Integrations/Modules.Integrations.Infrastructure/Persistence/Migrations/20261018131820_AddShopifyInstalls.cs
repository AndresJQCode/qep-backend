using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Integrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShopifyInstalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shopify_installs",
                schema: "integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shop_domain = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    redirect_uri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    initiated_tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    initiated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    shop_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    shop_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    scopes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    access_token_protected = table.Column<byte[]>(type: "bytea", nullable: true),
                    key_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shopify_installs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shopify_installs_expires_at",
                schema: "integrations",
                table: "shopify_installs",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_shopify_installs_key_id",
                schema: "integrations",
                table: "shopify_installs",
                column: "key_id",
                filter: "key_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_shopify_installs_state",
                schema: "integrations",
                table: "shopify_installs",
                column: "state",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shopify_installs",
                schema: "integrations");
        }
    }
}
