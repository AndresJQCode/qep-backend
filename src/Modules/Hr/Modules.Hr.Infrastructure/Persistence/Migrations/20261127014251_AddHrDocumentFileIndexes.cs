using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Hr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHrDocumentFileIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_salary_records_document_file_id",
                schema: "hr",
                table: "salary_records",
                column: "document_file_id",
                filter: "document_file_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_medical_exams_document_file_id",
                schema: "hr",
                table: "medical_exams",
                column: "document_file_id",
                filter: "document_file_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_employment_contracts_document_file_id",
                schema: "hr",
                table: "employment_contracts",
                column: "document_file_id",
                filter: "document_file_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_contract_renewals_document_file_id",
                schema: "hr",
                table: "contract_renewals",
                column: "document_file_id",
                filter: "document_file_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_assignment_records_document_file_id",
                schema: "hr",
                table: "assignment_records",
                column: "document_file_id",
                filter: "document_file_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_salary_records_document_file_id",
                schema: "hr",
                table: "salary_records");

            migrationBuilder.DropIndex(
                name: "IX_medical_exams_document_file_id",
                schema: "hr",
                table: "medical_exams");

            migrationBuilder.DropIndex(
                name: "IX_employment_contracts_document_file_id",
                schema: "hr",
                table: "employment_contracts");

            migrationBuilder.DropIndex(
                name: "IX_contract_renewals_document_file_id",
                schema: "hr",
                table: "contract_renewals");

            migrationBuilder.DropIndex(
                name: "IX_assignment_records_document_file_id",
                schema: "hr",
                table: "assignment_records");
        }
    }
}
