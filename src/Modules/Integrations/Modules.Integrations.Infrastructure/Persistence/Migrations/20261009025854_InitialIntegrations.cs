using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Integrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialIntegrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "integrations");

            migrationBuilder.CreateTable(
                name: "connections",
                schema: "integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    fields = table.Column<string>(type: "jsonb", nullable: false),
                    last_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_failure_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connections", x => x.id);
                    table.CheckConstraint("CK_connections_provider_key", "provider_key IN ('zenvia')");
                    table.CheckConstraint("CK_connections_status", "status IN ('Active','Paused','NeedsAttention')");
                });

            migrationBuilder.CreateTable(
                name: "connection_secrets",
                schema: "integrations",
                columns: table => new
                {
                    field_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connection_secrets", x => new { x.connection_id, x.field_key });
                    table.ForeignKey(
                        name: "FK_connection_secrets_connections_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "integrations",
                        principalTable: "connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_connection_secrets_key_id",
                schema: "integrations",
                table: "connection_secrets",
                column: "key_id");

            migrationBuilder.CreateIndex(
                name: "IX_connections_tenant",
                schema: "integrations",
                table: "connections",
                column: "tenant_id");

            // Spec 2026-10-08: el nombre es único por tenant y proveedor sin importar mayúsculas. EF no
            // modela índices por expresión, así que va a mano; IntegrationsUnitOfWork lo traduce a
            // integrations.connection.name_taken por este nombre.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_connections_tenant_provider_name\" ON integrations.connections (tenant_id, provider_key, lower(name));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "connection_secrets",
                schema: "integrations");

            migrationBuilder.DropTable(
                name: "connections",
                schema: "integrations");
        }
    }
}
