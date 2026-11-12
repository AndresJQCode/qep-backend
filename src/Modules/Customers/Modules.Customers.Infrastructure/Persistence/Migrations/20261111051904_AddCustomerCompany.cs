using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerCompany : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                schema: "customers",
                table: "customers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_customers_business_name_trgm",
                schema: "customers",
                table: "customers",
                column: "business_name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_customers_company_id",
                schema: "customers",
                table: "customers",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_customers_tenant_company",
                schema: "customers",
                table: "customers",
                columns: new[] { "tenant_id", "company_id" },
                filter: "company_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_customers_customers_company_id",
                schema: "customers",
                table: "customers",
                column: "company_id",
                principalSchema: "customers",
                principalTable: "customers",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_customers_customers_company_id",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropIndex(
                name: "IX_customers_business_name_trgm",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropIndex(
                name: "IX_customers_company_id",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropIndex(
                name: "IX_customers_tenant_company",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "company_id",
                schema: "customers",
                table: "customers");
        }
    }
}
