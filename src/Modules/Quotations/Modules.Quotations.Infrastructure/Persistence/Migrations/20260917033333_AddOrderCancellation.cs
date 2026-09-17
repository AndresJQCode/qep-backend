using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Quién anuló el pedido, cuándo y por qué (spec 2026-09-16).
    ///
    /// Nullables y sin backfill: ningún pedido existente está anulado. `status` ya es
    /// varchar(20), así que `Cancelled` entra sin tocar esa columna.
    /// </summary>
    public partial class AddOrderCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cancellation_reason",
                schema: "quotations",
                table: "orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancelled_at",
                schema: "quotations",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cancelled_by",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cancellation_reason",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "cancelled_at",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "cancelled_by",
                schema: "quotations",
                table: "orders");
        }
    }
}
