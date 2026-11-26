using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wholesale.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MovePriceListsToCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Spec 2026-11-22 (listas de precios por canal), D-8. Expand: desde esta versión distributors.price_list_id apunta a
            // catalog.price_lists, otro esquema, así que la FK se va. La tabla wholesale.price_lists NO se borra acá: los pods viejos
            // del rollout todavía la leen. Queda sin uso y la borra una migración de contrato posterior (DROP TABLE IF EXISTS).
            migrationBuilder.DropForeignKey(
                name: "FK_distributors_price_lists_price_list_id",
                schema: "wholesale",
                table: "distributors");

            migrationBuilder.DropIndex(
                name: "IX_distributors_price_list_id",
                schema: "wholesale",
                table: "distributors");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Falla si algún distribuidor ya apunta a una lista de Catalog: es la señal correcta, esa relación no cabe en la FK vieja.
            migrationBuilder.CreateIndex(
                name: "IX_distributors_price_list_id",
                schema: "wholesale",
                table: "distributors",
                column: "price_list_id");

            migrationBuilder.AddForeignKey(
                name: "FK_distributors_price_lists_price_list_id",
                schema: "wholesale",
                table: "distributors",
                column: "price_list_id",
                principalSchema: "wholesale",
                principalTable: "price_lists",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
