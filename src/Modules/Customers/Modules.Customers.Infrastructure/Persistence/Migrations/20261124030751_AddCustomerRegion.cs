using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerRegion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_customer_addresses_city_lane",
                schema: "customers",
                table: "customer_addresses");

            migrationBuilder.AddColumn<Guid>(
                name: "region_id",
                schema: "customers",
                table: "customers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "region_id",
                schema: "customers",
                table: "customer_addresses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_customer_addresses_city_lane",
                schema: "customers",
                table: "customer_addresses",
                sql: "(country = 'CO' AND city_id IS NOT NULL AND city_name IS NULL AND region_id IS NULL) OR (country <> 'CO' AND city_id IS NULL AND city_name IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_customer_addresses_city_lane",
                schema: "customers",
                table: "customer_addresses");

            migrationBuilder.DropColumn(
                name: "region_id",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "region_id",
                schema: "customers",
                table: "customer_addresses");

            migrationBuilder.AddCheckConstraint(
                name: "CK_customer_addresses_city_lane",
                schema: "customers",
                table: "customer_addresses",
                sql: "(country = 'CO' AND city_id IS NOT NULL AND city_name IS NULL) OR (country <> 'CO' AND city_id IS NULL AND city_name IS NOT NULL)");
        }
    }
}
