using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Siigo.Infrastructure.Persistence.Migrations
{
    /// <summary>Spec 2026-11-24, etapa <i>expand</i>: sólo columnas nullable o con valor por defecto, el índice de «un pedido en una sola
    /// tanda sin terminar» y el relleno de las facturas que ya estaban sin respuesta. El código anterior corre contra este esquema.</summary>
    public partial class AddSiigoBackgroundInvoicing : Migration
    {
        /// <summary>Hoy nada impide que un pedido esté en dos tandas sin terminar: si pasa, la migración se detiene con un mensaje que lo
        /// dice, antes de que el índice único falle.</summary>
        internal const string OpenOrderGuardSql =
            """
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM siigo.invoice_batch_items
                    WHERE status IN ('Pending', 'Sending')
                    GROUP BY tenant_id, order_id
                    HAVING count(*) > 1) THEN
                    RAISE EXCEPTION 'siigo.invoice_batch_items has orders with more than one open item (Pending or Sending). The owner decides which one to close before running this migration.';
                END IF;
            END $$;
            """;

        /// <summary>Las <c>Pending</c> que ya salieron sin respuesta (o que quedaron a mitad del envío) pasan a estar por confirmar.
        /// Idempotente: sólo toca las que no tienen <c>unanswered_at</c>.</summary>
        internal const string BackfillSql =
            """
            UPDATE siigo.invoices
            SET unanswered_at = updated_at, last_unanswered_at = updated_at
            WHERE status = 'Pending'
              AND unanswered_at IS NULL
              AND (error_code IN (
                      'siigo.invoice.timeout',
                      'siigo.invoice.network',
                      'siigo.invoice.rate_limited',
                      'siigo.invoice.unavailable',
                      'siigo.invoice.siigo_service')
                   OR sending_since IS NOT NULL);
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_unanswered_at",
                schema: "siigo",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "unanswered_at",
                schema: "siigo",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "invoice_id",
                schema: "siigo",
                table: "invoice_batch_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "order_mark_failed",
                schema: "siigo",
                table: "invoice_batch_items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "payments_json",
                schema: "siigo",
                table: "invoice_batch_items",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(OpenOrderGuardSql);

            migrationBuilder.CreateIndex(
                name: "IX_invoice_batch_items_tenant_order_open",
                schema: "siigo",
                table: "invoice_batch_items",
                columns: new[] { "tenant_id", "order_id" },
                unique: true,
                filter: "status IN ('Pending', 'Sending')");

            migrationBuilder.Sql(BackfillSql);
        }

        // No se corre: siigo.invoices es el registro de un documento legal (.claude/rules/siigo.md). Borrar unanswered_at y
        // last_unanswered_at olvidaría qué facturas están por confirmar y volvería a dejar rechazarlas.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_invoice_batch_items_tenant_order_open",
                schema: "siigo",
                table: "invoice_batch_items");

            migrationBuilder.DropColumn(
                name: "last_unanswered_at",
                schema: "siigo",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "unanswered_at",
                schema: "siigo",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "invoice_id",
                schema: "siigo",
                table: "invoice_batch_items");

            migrationBuilder.DropColumn(
                name: "order_mark_failed",
                schema: "siigo",
                table: "invoice_batch_items");

            migrationBuilder.DropColumn(
                name: "payments_json",
                schema: "siigo",
                table: "invoice_batch_items");
        }
    }
}
