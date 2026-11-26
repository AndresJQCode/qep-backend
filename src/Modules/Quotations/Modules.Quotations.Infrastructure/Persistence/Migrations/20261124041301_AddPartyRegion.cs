using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyRegion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "region_id",
                schema: "quotations",
                table: "quotation_parties",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "region_id",
                schema: "quotations",
                table: "order_parties",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "region_id",
                schema: "quotations",
                table: "quotation_parties");

            migrationBuilder.DropColumn(
                name: "region_id",
                schema: "quotations",
                table: "order_parties");
        }
    }
}
