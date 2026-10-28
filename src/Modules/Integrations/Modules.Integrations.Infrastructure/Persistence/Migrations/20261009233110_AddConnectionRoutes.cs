using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Integrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "connection_routes",
                schema: "integrations",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    external_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    account_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connection_routes", x => x.connection_id);
                    table.ForeignKey(
                        name: "FK_connection_routes_connections_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "integrations",
                        principalTable: "connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_connection_routes_provider_account",
                schema: "integrations",
                table: "connection_routes",
                columns: new[] { "provider_key", "account_id" },
                filter: "account_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_connection_routes_provider_external",
                schema: "integrations",
                table: "connection_routes",
                columns: new[] { "provider_key", "external_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "connection_routes",
                schema: "integrations");
        }
    }
}
