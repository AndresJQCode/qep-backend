using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec ReteICA por empresa §7.4: los municipios donde un cliente es agente retenedor. La FK hacia geography.cities se
    /// declara a mano, como la de <c>customers.city_id</c>: City vive en otro DbContext y EF no modela relaciones fuera del
    /// suyo. Customers migra después de Geography, así que la tabla de ciudades ya existe.
    /// </summary>
    public partial class AddCustomerWithholdingAgentCities : Migration
    {
        private const string CityForeignKey = "FK_customer_withholding_agent_cities_cities_city_id";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_withholding_agent_cities",
                schema: "customers",
                columns: table => new
                {
                    country = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    city_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_withholding_agent_cities", x => new { x.customer_id, x.country, x.type, x.city_id });
                    table.ForeignKey(
                        name: "FK_customer_withholding_agent_cities_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "customers",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_withholding_agent_cities_city",
                schema: "customers",
                table: "customer_withholding_agent_cities",
                column: "city_id");

            migrationBuilder.AddForeignKey(
                name: CityForeignKey,
                schema: "customers",
                table: "customer_withholding_agent_cities",
                column: "city_id",
                principalSchema: "geography",
                principalTable: "cities",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: CityForeignKey,
                schema: "customers",
                table: "customer_withholding_agent_cities");

            migrationBuilder.DropTable(
                name: "customer_withholding_agent_cities",
                schema: "customers");
        }
    }
}
