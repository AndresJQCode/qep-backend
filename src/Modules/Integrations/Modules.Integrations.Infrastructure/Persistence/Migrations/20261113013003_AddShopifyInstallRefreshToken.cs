using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Integrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShopifyInstallRefreshToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "access_token_expires_at",
                schema: "integrations",
                table: "shopify_installs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "refresh_token_expires_at",
                schema: "integrations",
                table: "shopify_installs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "refresh_token_protected",
                schema: "integrations",
                table: "shopify_installs",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "access_token_expires_at",
                schema: "integrations",
                table: "shopify_installs");

            migrationBuilder.DropColumn(
                name: "refresh_token_expires_at",
                schema: "integrations",
                table: "shopify_installs");

            migrationBuilder.DropColumn(
                name: "refresh_token_protected",
                schema: "integrations",
                table: "shopify_installs");
        }
    }
}
