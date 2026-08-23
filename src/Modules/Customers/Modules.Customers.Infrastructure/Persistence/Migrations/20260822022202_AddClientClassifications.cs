using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClientClassifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "client_classifications",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    prefix = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_classifications", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_client_classifications_tenant",
                schema: "customers",
                table: "client_classifications",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_client_classifications_tenant_name",
                schema: "customers",
                table: "client_classifications",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_client_classifications_tenant_prefix",
                schema: "customers",
                table: "client_classifications",
                columns: new[] { "tenant_id", "prefix" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "client_classifications",
                schema: "customers");
        }
    }
}
