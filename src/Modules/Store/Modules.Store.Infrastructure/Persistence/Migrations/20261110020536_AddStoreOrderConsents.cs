using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Store.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreOrderConsents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_consents",
                schema: "store",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    visitor_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    policy_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    policy_set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_consents", x => x.order_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_consents_tenant",
                schema: "store",
                table: "order_consents",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_consents",
                schema: "store");
        }
    }
}
