using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerMerge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_customers_tenant_identification",
                schema: "customers",
                table: "customers");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "merged_at",
                schema: "customers",
                table: "customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_into_id",
                schema: "customers",
                table: "customers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_customers_tenant_identification",
                schema: "customers",
                table: "customers",
                columns: new[] { "tenant_id", "identification_type", "identification_number" },
                unique: true,
                filter: "merged_into_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_customers_tenant_merged_into",
                schema: "customers",
                table: "customers",
                columns: new[] { "tenant_id", "merged_into_id" },
                filter: "merged_into_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_customers_tenant_identification",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropIndex(
                name: "IX_customers_tenant_merged_into",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "merged_at",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "merged_into_id",
                schema: "customers",
                table: "customers");

            migrationBuilder.CreateIndex(
                name: "IX_customers_tenant_identification",
                schema: "customers",
                table: "customers",
                columns: new[] { "tenant_id", "identification_type", "identification_number" },
                unique: true);
        }
    }
}
