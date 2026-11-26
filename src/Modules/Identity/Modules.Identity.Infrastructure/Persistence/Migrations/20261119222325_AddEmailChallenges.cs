using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailChallenges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_challenges",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_challenges", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_challenges_email_hash_used_at",
                schema: "identity",
                table: "email_challenges",
                columns: new[] { "email_hash", "used_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_challenges",
                schema: "identity");
        }
    }
}
