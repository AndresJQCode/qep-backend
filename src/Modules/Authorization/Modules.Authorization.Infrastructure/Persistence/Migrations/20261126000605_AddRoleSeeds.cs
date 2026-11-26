using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Authorization.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleSeeds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "role_seeds",
                schema: "authorization",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seeded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_seeds", x => x.tenant_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "role_seeds",
                schema: "authorization");
        }
    }
}
