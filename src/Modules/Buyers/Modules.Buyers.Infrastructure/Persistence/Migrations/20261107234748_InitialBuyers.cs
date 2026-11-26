using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Buyers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialBuyers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "buyers");

            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "buyers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    email_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "consents",
                schema: "buyers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    visitor_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    store_host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    policy_url = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    policy_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "email_tokens",
                schema: "buyers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    token_hash = table.Column<string>(type: "char(64)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_tokens", x => x.id);
                    table.CheckConstraint("CK_email_tokens_purpose", "purpose IN ('Verify','Reset')");
                    table.ForeignKey(
                        name: "FK_email_tokens_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "buyers",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "profiles",
                schema: "buyers",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profiles", x => x.account_id);
                    table.ForeignKey(
                        name: "FK_profiles_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "buyers",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                schema: "buyers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    token_hash = table.Column<string>(type: "char(64)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_sessions_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "buyers",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_email",
                schema: "buyers",
                table: "accounts",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consents_account_id",
                schema: "buyers",
                table: "consents",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_email_tokens_account_id_purpose_used_at",
                schema: "buyers",
                table: "email_tokens",
                columns: new[] { "account_id", "purpose", "used_at" });

            migrationBuilder.CreateIndex(
                name: "IX_email_tokens_token_hash",
                schema: "buyers",
                table: "email_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_account_id_revoked_at",
                schema: "buyers",
                table: "sessions",
                columns: new[] { "account_id", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_token_hash",
                schema: "buyers",
                table: "sessions",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consents",
                schema: "buyers");

            migrationBuilder.DropTable(
                name: "email_tokens",
                schema: "buyers");

            migrationBuilder.DropTable(
                name: "profiles",
                schema: "buyers");

            migrationBuilder.DropTable(
                name: "sessions",
                schema: "buyers");

            migrationBuilder.DropTable(
                name: "accounts",
                schema: "buyers");
        }
    }
}
