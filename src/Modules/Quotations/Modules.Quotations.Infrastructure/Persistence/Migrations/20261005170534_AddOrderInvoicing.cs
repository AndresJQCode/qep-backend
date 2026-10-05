using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Quién marcó el pedido como facturado y cuándo (spec 2026-10-05).
    ///
    /// Nullables y sin backfill: ningún pedido existente está facturado. `status` ya es
    /// varchar(20) sin check constraint, así que `Invoiced` entra sin tocar esa columna.
    /// </summary>
    public partial class AddOrderInvoicing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "invoiced_at",
                schema: "quotations",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "invoiced_by",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "invoiced_at",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "invoiced_by",
                schema: "quotations",
                table: "orders");
        }
    }
}
