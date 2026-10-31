using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.ApiAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialApiAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "api_access");

            migrationBuilder.CreateTable(
                name: "api_keys",
                schema: "api_access",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    key_prefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    secret_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    connection_ids = table.Column<Guid[]>(type: "uuid[]", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.id);
                    table.CheckConstraint("CK_api_keys_connections", "connection_ids IS NULL OR cardinality(connection_ids) > 0");
                    table.CheckConstraint("CK_api_keys_revocation", "(revoked_at IS NULL) = (revoked_by_member_id IS NULL)");
                    table.CheckConstraint("CK_api_keys_scopes", "cardinality(scopes) > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_key_prefix",
                schema: "api_access",
                table: "api_keys",
                column: "key_prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_tenant_created",
                schema: "api_access",
                table: "api_keys",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_tenant_name_active",
                schema: "api_access",
                table: "api_keys",
                columns: new[] { "tenant_id", "name" },
                unique: true,
                filter: "revoked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_keys",
                schema: "api_access");
        }
    }
}
