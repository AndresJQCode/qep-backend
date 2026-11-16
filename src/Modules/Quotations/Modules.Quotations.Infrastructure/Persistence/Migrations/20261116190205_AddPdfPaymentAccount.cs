using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPdfPaymentAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pdf_payment_account",
                schema: "quotations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    in_quotation = table.Column<bool>(type: "boolean", nullable: false),
                    in_order = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pdf_payment_account", x => x.tenant_id);
                });

            // Origen Botánico sigue imprimiendo la cuenta para el pago en los dos documentos (spec 2026-11-16 pdf-payment-account,
            // decisión 2); los demás tenants quedan sin fila, o sea apagados. Misma guarda que AddPdfSections: tenancy.tenants migra
            // antes que Quotations al arrancar, y la guarda deja correr la migración en una base que sólo tiene Quotations.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('tenancy.tenants') IS NOT NULL THEN
                        INSERT INTO quotations.pdf_payment_account (tenant_id, enabled, in_quotation, in_order, updated_at)
                        SELECT t.id, true, true, true, now()
                        FROM tenancy.tenants t
                        WHERE t.slug = 'origen-botanico'
                        ON CONFLICT DO NOTHING;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pdf_payment_account",
                schema: "quotations");
        }
    }
}
