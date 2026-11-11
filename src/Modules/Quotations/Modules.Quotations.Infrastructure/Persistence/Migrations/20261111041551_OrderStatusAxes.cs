using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Los ejes del pedido (spec 2026-11-10): agrega el envío, las marcas de pagado, preparado y archivado y los
    /// ajustes del pedido por tenant, y mapea los datos (§8). EF propuso renombrar <c>approved_*</c> a
    /// <c>prepared_*</c>; no es eso: <c>approved_*</c> se copia a <c>paid_marked_*</c>.
    /// <para>
    /// Sin pérdida (owner, 2026-11-11): <c>approved_*</c> se quedan en la tabla como históricas, y el estado y el pago
    /// de antes se respaldan en <c>legacy_status</c> y <c>legacy_payment_status</c> antes de cualquier <c>UPDATE</c>.
    /// Ninguna de las cuatro está en el modelo de EF: sólo viven en la base, y el <c>Down</c> las usa para devolver
    /// cada fila exactamente a como estaba.
    /// </para>
    /// </summary>
    public partial class OrderStatusAxes : Migration
    {
        private const string HistoricalComment =
            "'Histórico: aprobación anterior a OrderStatusAxes (2026-11). Sin uso en el código.'";

        private const string LegacyComment =
            "'Histórico: valor anterior a OrderStatusAxes (2026-11), respaldo para revertir. Sin uso en el código.'";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Respaldo antes de tocar nada: el Down devuelve estado y pago desde aquí.
            migrationBuilder.AddColumn<string>(
                name: "legacy_status",
                schema: "quotations",
                table: "orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "legacy_payment_status",
                schema: "quotations",
                table: "orders",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE quotations.orders
                SET legacy_status = status, legacy_payment_status = payment_status;
                """);

            migrationBuilder.Sql(
                $"""
                COMMENT ON COLUMN quotations.orders.approved_at IS {HistoricalComment};
                COMMENT ON COLUMN quotations.orders.approved_by IS {HistoricalComment};
                COMMENT ON COLUMN quotations.orders.legacy_status IS {LegacyComment};
                COMMENT ON COLUMN quotations.orders.legacy_payment_status IS {LegacyComment};
                """);

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Las filas que existían antes del Up vuelven exactamente a su estado y su pago; approved_* nunca se
            // tocaron y se quedan como estaban.
            migrationBuilder.Sql(
                """
                UPDATE quotations.orders
                SET status = legacy_status, payment_status = legacy_payment_status
                WHERE legacy_status IS NOT NULL;
                """);

            // Las creadas después del Up no tienen respaldo: se traducen al esquema viejo, que no tenía archivo ni
            // PendingVerification. Quien las marcó pagadas queda como quien las aprobó.
            migrationBuilder.Sql(
                """
                UPDATE quotations.orders
                SET approved_at = paid_marked_at,
                    approved_by = paid_marked_by,
                    status = CASE
                        WHEN status NOT IN ('Open', 'Archived') THEN status
                        WHEN invoiced_at IS NOT NULL THEN 'Invoiced'
                        WHEN paid_marked_at IS NOT NULL THEN 'Approved'
                        ELSE 'Pending'
                    END,
                    payment_status = CASE
                        WHEN payment_status = 'PendingVerification' THEN 'FullPaymentReceived'
                        ELSE payment_status
                    END
                WHERE legacy_status IS NULL;
                """);

            migrationBuilder.Sql(
                """
                COMMENT ON COLUMN quotations.orders.approved_at IS NULL;
                COMMENT ON COLUMN quotations.orders.approved_by IS NULL;
                """);

            migrationBuilder.DropColumn(
                name: "legacy_status",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "legacy_payment_status",
                schema: "quotations",
                table: "orders");

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
