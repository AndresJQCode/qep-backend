using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Shopify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCityCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "city_corrections",
                schema: "shopify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    synonym_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mirror_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    city_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_city_id = table.Column<Guid>(type: "uuid", nullable: true),
                    party_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    party_phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    party_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    party_address = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    customer_version = table.Column<long>(type: "bigint", nullable: true),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_city_corrections", x => x.id);
                    table.CheckConstraint("CK_city_corrections_origin", "origin IN ('Correction','Import')");
                    table.CheckConstraint("CK_city_corrections_target", "target IN ('Order','Customer')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_city_corrections_synonym_target",
                schema: "shopify",
                table: "city_corrections",
                columns: new[] { "synonym_id", "target", "target_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_city_corrections_tenant_target",
                schema: "shopify",
                table: "city_corrections",
                columns: new[] { "tenant_id", "target", "target_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "city_corrections",
                schema: "shopify");
        }
    }
}
