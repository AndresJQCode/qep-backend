using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportFormOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "organization_company_id",
                schema: "support",
                table: "form_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_form_versions_organization_company",
                schema: "support",
                table: "form_versions",
                column: "organization_company_id");

            // Spec 2026-11-15 §9.3 (plan L29): real FK to the company that answers on the portal. Deleting a company some version of the
            // form chose comes out as 422 companies.company.in_use: CompaniesUnitOfWork translates the SQLSTATE 23001 (restrict_violation,
            // the one of ON DELETE RESTRICT) like the 23503, without touching Companies. By hand because EF does not model relations
            // between the DbContexts of different modules (precedent pos.cash_sessions → companies.companies); the model snapshot does
            // not record it. Companies migrates before Support (Program.cs).
            migrationBuilder.AddForeignKey(
                name: "FK_form_versions_companies_organization_company_id",
                schema: "support",
                table: "form_versions",
                column: "organization_company_id",
                principalSchema: "companies",
                principalTable: "companies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_form_versions_companies_organization_company_id",
                schema: "support",
                table: "form_versions");

            migrationBuilder.DropIndex(
                name: "IX_form_versions_organization_company",
                schema: "support",
                table: "form_versions");

            migrationBuilder.DropColumn(
                name: "organization_company_id",
                schema: "support",
                table: "form_versions");
        }
    }
}
