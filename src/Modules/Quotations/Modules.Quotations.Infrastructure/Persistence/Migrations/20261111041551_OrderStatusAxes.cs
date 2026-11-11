using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Los ejes del pedido (spec 2026-11-10): agrega el envío, las marcas de pagado, preparado y archivado y los
    /// ajustes del pedido por tenant, y mapea los datos (§8). EF propuso renombrar <c>approved_*</c> a
    /// <c>prepared_*</c>; no es eso: <c>approved_*</c> se copia a <c>paid_marked_*</c> y después se elimina.
    /// </summary>
    public partial class OrderStatusAxes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // §6: los defaults del owner van en la columna (no en el modelo, ver ConfigureQuotationSettings), para
            // las filas que ya existen y la que crea LockRowAsync con SQL crudo.
            migrationBuilder.AddColumn<bool>(
                name: "require_payment_proof_to_mark_paid",
                schema: "quotations",
                table: "tenant_quotation_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "require_full_payment_to_invoice",
                schema: "quotations",
                table: "tenant_quotation_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "auto_archive_orders",
                schema: "quotations",
                table: "tenant_quotation_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // §8: NotPrepared para todos, por el default de la columna.
            migrationBuilder.AddColumn<string>(
                name: "fulfillment_status",
                schema: "quotations",
                table: "orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotPrepared");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "paid_marked_at",
                schema: "quotations",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "paid_marked_by",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "prepared_at",
                schema: "quotations",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "prepared_by",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "archived_at",
                schema: "quotations",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "archived_by",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: true);

            // §8: quien aprobó un pedido pagado es quien de hecho verificó el pago.
            migrationBuilder.Sql(
                """
                UPDATE quotations.orders
                SET paid_marked_at = approved_at, paid_marked_by = approved_by
                WHERE status IN ('Approved', 'Invoiced') AND payment_status = 'FullPaymentReceived';
                """);

            // §8: un «Pago total» que nadie aprobó vuelve a lo que digan sus comprobantes, igual que
            // Order.CalculatedPaymentStatus: nada consignado es PaymentPending; menos que el neto redondeado como lo
            // muestra la moneda (pesos enteros en COP, centavos en las demás; round de numeric redondea alejándose
            // del cero, como MidpointRounding.AwayFromZero) es PartialPaymentReceived; lo que lo cubre,
            // PendingVerification. El pago sincronizado de Shopify lo dice la tienda, no una persona, y conserva su
            // valor (Review Focus 1); pero uno de Shopify con comprobantes de QEP lo eligió la asesora (la
            // sincronización no corre con comprobantes) y se recalcula como los demás.
            migrationBuilder.Sql(
                """
                WITH consigned AS (
                    SELECT o.id,
                           EXISTS (SELECT 1 FROM quotations.order_payment_proofs p WHERE p.order_id = o.id) AS has_proofs,
                           COALESCE(
                               (SELECT sum(p.amount) FROM quotations.order_payment_proofs p WHERE p.order_id = o.id),
                               0) AS paid,
                           round(o.net_total, CASE WHEN o.currency = 'COP' THEN 0 ELSE 2 END) AS displayed_net
                    FROM quotations.orders o
                    WHERE o.status = 'Pending' AND o.payment_status = 'FullPaymentReceived'
                )
                UPDATE quotations.orders o
                SET payment_status = CASE
                    WHEN c.paid <= 0 THEN 'PaymentPending'
                    WHEN c.paid < c.displayed_net THEN 'PartialPaymentReceived'
                    ELSE 'PendingVerification'
                END
                FROM consigned c
                WHERE o.id = c.id AND (o.source <> 'Shopify' OR c.has_proofs);
                """);

            // §8: los tres estados vivos pasan a Open; invoiced_* queda como estaba. Nada se archiva.
            migrationBuilder.Sql(
                """
                UPDATE quotations.orders
                SET status = 'Open'
                WHERE status IN ('Pending', 'Approved', 'Invoiced');
                """);

            migrationBuilder.DropColumn(
                name: "approved_at",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "approved_by",
                schema: "quotations",
                table: "orders");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "approved_at",
                schema: "quotations",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "approved_by",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: true);

            // Un archivado vuelve al estado vivo que le corresponde: el esquema viejo no tenía archivo.
            migrationBuilder.Sql(
                """
                UPDATE quotations.orders
                SET approved_at = paid_marked_at,
                    approved_by = paid_marked_by,
                    status = CASE
                        WHEN invoiced_at IS NOT NULL THEN 'Invoiced'
                        WHEN paid_marked_at IS NOT NULL THEN 'Approved'
                        ELSE 'Pending'
                    END
                WHERE status IN ('Open', 'Archived');
                """);

            // Con pérdida: un «Pago total» sin aprobar que el Up recalculó a PaymentPending o PartialPaymentReceived
            // no se distingue de uno que ya lo era, y se queda así. Sólo PendingVerification vuelve a pagado.
            migrationBuilder.Sql(
                """
                UPDATE quotations.orders
                SET payment_status = 'FullPaymentReceived'
                WHERE payment_status = 'PendingVerification';
                """);

            migrationBuilder.DropColumn(
                name: "archived_at",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "archived_by",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "prepared_at",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "prepared_by",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "paid_marked_at",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "paid_marked_by",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "fulfillment_status",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "auto_archive_orders",
                schema: "quotations",
                table: "tenant_quotation_settings");

            migrationBuilder.DropColumn(
                name: "require_full_payment_to_invoice",
                schema: "quotations",
                table: "tenant_quotation_settings");

            migrationBuilder.DropColumn(
                name: "require_payment_proof_to_mark_paid",
                schema: "quotations",
                table: "tenant_quotation_settings");
        }
    }
}
