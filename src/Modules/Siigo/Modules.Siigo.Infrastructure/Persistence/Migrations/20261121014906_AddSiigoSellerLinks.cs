using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Siigo.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSiigoSellerLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "products_auto_linked_at",
                schema: "siigo",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sellers_auto_linked_at",
                schema: "siigo",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "seller_links",
                schema: "siigo",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    siigo_user_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    siigo_user_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seller_links", x => new { x.connection_id, x.membership_id });
                    table.ForeignKey(
                        name: "FK_seller_links_settings_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "siigo",
                        principalTable: "settings",
                        principalColumn: "connection_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_seller_links_tenant_connection",
                schema: "siigo",
                table: "seller_links",
                columns: new[] { "tenant_id", "connection_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "seller_links",
                schema: "siigo");

            migrationBuilder.DropColumn(
                name: "products_auto_linked_at",
                schema: "siigo",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "sellers_auto_linked_at",
                schema: "siigo",
                table: "settings");
        }
    }
}
