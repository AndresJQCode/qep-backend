using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropTenantWhatsAppSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_whatsapp_settings",
                schema: "quotations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_whatsapp_settings",
                schema: "quotations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    api_token_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    from_number = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    template_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    api_token_ciphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    api_token_key_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_whatsapp_settings", x => x.tenant_id);
                    table.CheckConstraint("CK_tenant_whatsapp_settings_mode", "mode IN ('Shared', 'Own', 'Disabled')");
                    table.CheckConstraint("CK_tenant_whatsapp_settings_own_complete", "mode <> 'Own' OR (provider IS NOT NULL AND api_token_ciphertext IS NOT NULL AND from_number IS NOT NULL AND template_id IS NOT NULL)");
                    table.CheckConstraint("CK_tenant_whatsapp_settings_provider", "provider IS NULL OR provider IN ('Zenvia')");
                    table.CheckConstraint("CK_tenant_whatsapp_settings_token_pair", "(api_token_ciphertext IS NULL) = (api_token_key_id IS NULL)");
                });
        }
    }
}
