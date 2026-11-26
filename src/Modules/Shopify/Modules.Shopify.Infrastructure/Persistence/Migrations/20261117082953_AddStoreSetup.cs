using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Shopify.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec 2026-11-17 (onboarding de la tienda) §5 y §7 H7: el estado del asistente en <c>stores</c>
    /// (<c>products_coverage</c>, <c>products_coverage_accepted_at</c>, <c>setup_history_ran_at</c> y
    /// <c>setup_history_summary</c>). Aditiva: cuatro columnas nullable; el código viejo no las lee ni las escribe, así que corre
    /// igual contra el esquema nuevo durante el rollout.
    /// </summary>
    public partial class AddStoreSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "products_coverage",
                schema: "shopify",
                table: "stores",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "products_coverage_accepted_at",
                schema: "shopify",
                table: "stores",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "setup_history_ran_at",
                schema: "shopify",
                table: "stores",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "setup_history_summary",
                schema: "shopify",
                table: "stores",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "products_coverage",
                schema: "shopify",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "products_coverage_accepted_at",
                schema: "shopify",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "setup_history_ran_at",
                schema: "shopify",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "setup_history_summary",
                schema: "shopify",
                table: "stores");
        }
    }
}
