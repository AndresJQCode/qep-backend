using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Suppliers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierRegion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "region_id",
                schema: "suppliers",
                table: "suppliers",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "region_id",
                schema: "suppliers",
                table: "suppliers");
        }
    }
}
