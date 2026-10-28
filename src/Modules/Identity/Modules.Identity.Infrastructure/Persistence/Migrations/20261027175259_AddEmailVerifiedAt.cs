using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailVerifiedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "email_verified_at",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            // Quien ya entró con un proveedor externo probó su email (el login exige email_verified):
            // la verificación se remonta al primer vínculo.
            migrationBuilder.Sql("""
                UPDATE identity.users AS u
                SET email_verified_at = first_link.linked_at
                FROM (SELECT user_id, MIN(linked_at) AS linked_at FROM identity.provider_links GROUP BY user_id) AS first_link
                WHERE first_link.user_id = u.id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "email_verified_at",
                schema: "identity",
                table: "users");
        }
    }
}
