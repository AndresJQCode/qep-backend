using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Siigo.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSiigo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "siigo");

            migrationBuilder.CreateTable(
                name: "settings",
                schema: "siigo",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_type_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    seller_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    cost_center_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    warehouse_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    send_to_dian = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    send_mail = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    freight_separate_invoice = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    freight_product_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    credit_payment_type_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    credit_days = table.Column<int>(type: "integer", nullable: true),
                    cash_on_delivery_payment_type_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    default_payment_type_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.connection_id);
                    table.CheckConstraint("CK_settings_credit_days", "credit_days IS NULL OR credit_days BETWEEN 1 AND 365");
                });

            migrationBuilder.CreateTable(
                name: "payment_links",
                schema: "siigo",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    siigo_payment_type_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_links", x => new { x.connection_id, x.bank_account_key });
                    table.ForeignKey(
                        name: "FK_payment_links_settings_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "siigo",
                        principalTable: "settings",
                        principalColumn: "connection_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tax_links",
                schema: "siigo",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tax_percentage = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    siigo_tax_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tax_links", x => new { x.connection_id, x.tax_percentage });
                    table.ForeignKey(
                        name: "FK_tax_links_settings_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "siigo",
                        principalTable: "settings",
                        principalColumn: "connection_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "variant_links",
                schema: "siigo",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    siigo_product_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    siigo_product_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    siigo_product_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    linked_by = table.Column<Guid>(type: "uuid", nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_variant_links", x => new { x.connection_id, x.variant_id });
                    table.ForeignKey(
                        name: "FK_variant_links_settings_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "siigo",
                        principalTable: "settings",
                        principalColumn: "connection_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "withholding_links",
                schema: "siigo",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tax_rate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    siigo_tax_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_withholding_links", x => new { x.connection_id, x.tax_rate_id });
                    table.ForeignKey(
                        name: "FK_withholding_links_settings_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "siigo",
                        principalTable: "settings",
                        principalColumn: "connection_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_settings_tenant_company",
                schema: "siigo",
                table: "settings",
                columns: new[] { "tenant_id", "company_id" },
                unique: true,
                filter: "company_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_variant_links_tenant_connection",
                schema: "siigo",
                table: "variant_links",
                columns: new[] { "tenant_id", "connection_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_links",
                schema: "siigo");

            migrationBuilder.DropTable(
                name: "tax_links",
                schema: "siigo");

            migrationBuilder.DropTable(
                name: "variant_links",
                schema: "siigo");

            migrationBuilder.DropTable(
                name: "withholding_links",
                schema: "siigo");

            migrationBuilder.DropTable(
                name: "settings",
                schema: "siigo");
        }
    }
}
