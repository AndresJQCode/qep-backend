using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCashSessionCompanyDocumentType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "company_document_type",
                schema: "pos",
                table: "cash_sessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                // Las cajas de antes eran de empresas colombianas: su número era un NIT.
                defaultValue: "NIT");

            // El relleno ya hizo su trabajo: la columna no lleva default, el dominio siempre lo manda.
            migrationBuilder.AlterColumn<string>(
                name: "company_document_type",
                schema: "pos",
                table: "cash_sessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldDefaultValue: "NIT");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "company_document_type",
                schema: "pos",
                table: "cash_sessions");
        }
    }
}
