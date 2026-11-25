using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// La libreta de perfiles de facturación del cliente y sus tasas de retención (spec 2026-11-24). Sólo crea (expand),
    /// sin backfill: ningún cliente tenía perfiles.
    /// </summary>
    public partial class AddCustomerBillingProfiles : Migration
    {
        // La FK real hacia geography.cities se declara a mano, como en AddCustomerAddresses: City vive en otro DbContext y
        // EF no modela relaciones fuera de su ModelBuilder. Customers migra después de Geography.
        private const string CityForeignKey = "FK_customer_billing_profiles_cities_city_id";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_billing_profiles",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    person_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    identification_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    identification_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    address = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false, defaultValue: "CO"),
                    city_id = table.Column<Guid>(type: "uuid", nullable: true),
                    city_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    region_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vat_surplus = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_principal = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_billing_profiles", x => x.id);
                    table.CheckConstraint("CK_customer_billing_profiles_city_lane", "(country = 'CO' AND city_id IS NOT NULL AND city_name IS NULL AND region_id IS NULL) OR (country <> 'CO' AND city_id IS NULL AND city_name IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_customer_billing_profiles_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "customers",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "customer_billing_profile_tax_rates",
                schema: "customers",
                columns: table => new
                {
                    tax_rate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_billing_profile_tax_rates", x => new { x.billing_profile_id, x.tax_rate_id });
                    table.ForeignKey(
                        name: "FK_customer_billing_profile_tax_rates_customer_billing_profile~",
                        column: x => x.billing_profile_id,
                        principalSchema: "customers",
                        principalTable: "customer_billing_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_billing_profile_tax_rates_tenant_tax_rate",
                schema: "customers",
                table: "customer_billing_profile_tax_rates",
                columns: new[] { "tenant_id", "tax_rate_id" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_billing_profiles_city",
                schema: "customers",
                table: "customer_billing_profiles",
                column: "city_id");

            migrationBuilder.CreateIndex(
                name: "IX_customer_billing_profiles_customer",
                schema: "customers",
                table: "customer_billing_profiles",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_customer_billing_profiles_principal",
                schema: "customers",
                table: "customer_billing_profiles",
                columns: new[] { "customer_id", "is_principal" });

            migrationBuilder.AddForeignKey(
                name: CityForeignKey,
                schema: "customers",
                table: "customer_billing_profiles",
                column: "city_id",
                principalSchema: "geography",
                principalTable: "cities",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_billing_profile_tax_rates",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "customer_billing_profiles",
                schema: "customers");
        }
    }
}
