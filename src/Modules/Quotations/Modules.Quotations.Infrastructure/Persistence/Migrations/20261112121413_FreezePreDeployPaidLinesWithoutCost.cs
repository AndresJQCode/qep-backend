using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FreezePreDeployPaidLinesWithoutCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Spec costo de producto §4.4: lo pagado antes del despliegue cuenta como «sin costo». Sin marca de
            // congelado, volver a marcarlo pagado (o que la plataforma lo saque y lo devuelva a pagado) congelaba el
            // costo de hoy en una línea vendida cuando ese costo no existía. Se marca congelada sin costo, con la
            // fecha de pago que dejó AddLineCostAndPaidAt. cost_source queda NULL: no se consultó ningún origen, y
            // así estas filas se distinguen de las que congeló el dominio (siempre Manual) para el Down.
            // Migración aparte, no un cambio a AddLineCostAndPaidAt, porque ésa ya pudo correr en bases locales.
            migrationBuilder.Sql("""
                UPDATE quotations.order_items AS item
                SET cost_captured_at = COALESCE(o.paid_at, now())
                FROM quotations.orders AS o
                WHERE item.order_id = o.id
                  AND o.payment_status = 'FullPaymentReceived'
                  AND item.cost_captured_at IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE quotations.order_items
                SET cost_captured_at = NULL
                WHERE cost_captured_at IS NOT NULL AND unit_cost IS NULL AND cost_source IS NULL;
                """);
        }
    }
}
