using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// El formato del número de cotización y de pedido, por tenant (spec 2026-09-17).
    ///
    /// Tabla vacía y sin backfill: un tenant sin fila sigue emitiendo `QUO-2026-0001` y
    /// `PED-2026-0001`, que es el default del código. Los cuatro CHECK son la red de la
    /// configuración, que se escribe con el SQL del runbook y no por un endpoint.
    /// </summary>
    public partial class AddDocumentNumberingFormats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_numbering_formats",
                schema: "quotations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    prefix = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    include_year = table.Column<bool>(type: "boolean", nullable: false),
                    year_separator = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    min_digits = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_numbering_formats", x => new { x.tenant_id, x.document_type });
                    table.CheckConstraint("CK_document_numbering_formats_document_type", "document_type IN ('order', 'quotation')");
                    table.CheckConstraint("CK_document_numbering_formats_min_digits", "min_digits BETWEEN 1 AND 10");
                    table.CheckConstraint("CK_document_numbering_formats_prefix", "prefix ~ '^[A-Za-z0-9-]{0,10}$'");
                    table.CheckConstraint("CK_document_numbering_formats_year_separator", "year_separator IN ('', '-', '/')");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_numbering_formats",
                schema: "quotations");
        }
    }
}
