using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWarehouseReceivingLocationIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "receiving_dock_id",
                schema: "wms",
                table: "warehouses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "receiving_zone_id",
                schema: "wms",
                table: "warehouses",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "receiving_dock_id",
                schema: "wms",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "receiving_zone_id",
                schema: "wms",
                table: "warehouses");
        }
    }
}
