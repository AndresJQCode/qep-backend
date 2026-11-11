using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Migración de datos de impuestos por tipo (spec 2026-11-10 §11.2 y §11.3, owner D10, plan P6): la retención fija
    /// del 2,5 % pasa a ser una tasa del tenant, asignada a sus clientes con retención y congelada en la foto de cada
    /// cotización y pedido existente. No recalcula ningún total: <c>retention_amount</c> y <c>net_total</c> quedan como
    /// estaban, y la foto sólo copia lo que ya decían.
    ///
    /// Es de Quotations y no un inicializador porque una migración corre exactamente una vez; Catalog y Customers migran
    /// antes (<c>Program.cs</c>). Cada paso toca otros esquemas, así que va entero dentro del guard de
    /// <c>to_regclass</c> (P6, ruling C11): en una base sin Catalog o sin Customers no hay tasa que crear y no hace nada.
    /// </summary>
    public partial class MigrateRetentionToWithholdings : Migration
    {
        private const string Guard =
            "to_regclass('catalog.tax_rates') IS NOT NULL AND to_regclass('customers.customer_tax_rates') IS NOT NULL";

        // Una tasa del tenant que ya hace lo mismo que la retención fija: Retefuente de Colombia al 2,5 %, sin base mínima
        // y activa. Si existe, se usa esa y no se crea otra.
        private const string Equivalent =
            "r.type = 'Retefuente' AND r.country = 'CO' AND r.percentage = 2.5 AND r.minimum_base IS NULL AND r.is_active";

        // La tasa de cada tenant que usan los pasos 2 a 4, siempre la misma: primero la del nombre de la migración, después
        // la variante «(migrada)» y si no, la equivalente más vieja.
        private const string Migrated =
            $"""
            migrated AS (
                SELECT DISTINCT ON (r.tenant_id) r.tenant_id, r.id, r.name, r.type, r.country, r.percentage
                FROM catalog.tax_rates AS r
                WHERE {Equivalent}
                ORDER BY r.tenant_id, (r.name = 'Retefuente 2,5 %') DESC, (r.name = 'Retefuente 2,5 % (migrada)') DESC,
                    r.created_at, r.id
            )
            """;

        // La foto de una fila: la tasa migrada sin base mínima ni UVT, en la posición 0 de su fuente. Donde la retención
        // fija aplicaba, la fila aplica con lo que ya estaba guardado (monto = retention_amount, base = subtotal); si no,
        // queda sin aplicar y en 0.
        private const string PhotoColumns =
            "document_id, source, position, tax_rate_id, name, type, country, percentage, minimum_base, minimum_base_unit, "
            + "tax_unit_value, base_amount, amount, applied";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Paso 1 (§11.2.1): una «Retefuente 2,5 %» por tenant que usó la retención fija, en un cliente o en un documento
            // (un documento puede retener por un cliente que ya no lo hace, y su foto necesita la tasa). Si el nombre ya lo
            // tiene otra tasa, «(migrada)».
            migrationBuilder.Sql(
                $$"""
                DO $$
                BEGIN
                    IF {{Guard}} THEN
                        WITH tenants AS (
                            SELECT tenant_id FROM customers.customers WHERE with_retention
                            UNION
                            SELECT tenant_id FROM quotations.quotations
                            WHERE customer_with_retention OR party_with_retention IS TRUE
                            UNION
                            SELECT tenant_id FROM quotations.orders
                            WHERE customer_with_retention OR party_with_retention IS TRUE
                        )
                        INSERT INTO catalog.tax_rates
                            (id, tenant_id, name, percentage, type, country, minimum_base, is_active, version, created_at, updated_at)
                        SELECT
                            gen_random_uuid(),
                            t.tenant_id,
                            CASE
                                WHEN EXISTS (
                                    SELECT 1 FROM catalog.tax_rates AS taken
                                    WHERE taken.tenant_id = t.tenant_id AND taken.name = 'Retefuente 2,5 %')
                                THEN 'Retefuente 2,5 % (migrada)'
                                ELSE 'Retefuente 2,5 %'
                            END,
                            2.5, 'Retefuente', 'CO', NULL, true, 1, now(), now()
                        FROM tenants AS t
                        WHERE NOT EXISTS (
                            SELECT 1 FROM catalog.tax_rates AS r WHERE r.tenant_id = t.tenant_id AND {{Equivalent}});
                    END IF;
                END $$;
                """);

            // Paso 2 (§11.2.2): la tasa a cada cliente con retención. Un cliente tiene una sola tasa por tipo: si ya tiene
            // una Retefuente, se queda con la suya.
            migrationBuilder.Sql(
                $$"""
                DO $$
                BEGIN
                    IF {{Guard}} THEN
                        WITH {{Migrated}}
                        INSERT INTO customers.customer_tax_rates (tenant_id, customer_id, tax_rate_id)
                        SELECT c.tenant_id, c.id, m.id
                        FROM customers.customers AS c
                        JOIN migrated AS m ON m.tenant_id = c.tenant_id
                        WHERE c.with_retention
                          AND NOT EXISTS (
                              SELECT 1
                              FROM customers.customer_tax_rates AS assigned
                              JOIN catalog.tax_rates AS r ON r.id = assigned.tax_rate_id
                              WHERE assigned.customer_id = c.id AND r.type = 'Retefuente')
                        ON CONFLICT DO NOTHING;
                    END IF;
                END $$;
                """);

            // Paso 3 (§11.3): la foto de las cotizaciones. La fuente 'Customer' sale de customer_with_retention y la
            // 'Billing' (el selector Retefuente) de party_with_retention, sólo con parte propia: sin ella no hay selector.
            // Aplicaba la de la fuente vigente (parte propia → 'Billing'; si no, 'Customer'), no a consumidor final y en COP:
            // la regla de AppliesRetention antes de esta rama.
            migrationBuilder.Sql(
                $$"""
                DO $$
                BEGIN
                    IF {{Guard}} THEN
                        WITH {{Migrated}},
                        documents AS (
                            SELECT
                                q.id, q.tenant_id, q.subtotal, q.retention_amount, q.customer_with_retention,
                                q.party_with_retention IS TRUE AS party_with_retention,
                                EXISTS (
                                    SELECT 1 FROM quotations.quotation_parties AS p
                                    WHERE p.quotation_id = q.id AND p.role = 'Billing') AS has_own_billing,
                                NOT q.bills_to_final_consumer AND q.currency = 'COP' AS can_retain
                            FROM quotations.quotations AS q
                        ),
                        photo AS (
                            SELECT d.id, d.tenant_id, d.subtotal, d.retention_amount,
                                'Customer' AS source, d.can_retain AND NOT d.has_own_billing AS applied
                            FROM documents AS d
                            WHERE d.customer_with_retention
                            UNION ALL
                            SELECT d.id, d.tenant_id, d.subtotal, d.retention_amount,
                                'Billing' AS source, d.can_retain AS applied
                            FROM documents AS d
                            WHERE d.has_own_billing AND d.party_with_retention
                        )
                        INSERT INTO quotations.quotation_withholdings ({{PhotoColumns}})
                        SELECT
                            p.id, p.source, 0, m.id, m.name, m.type, m.country, m.percentage, NULL, NULL, NULL,
                            CASE WHEN p.applied THEN p.subtotal ELSE 0 END,
                            CASE WHEN p.applied THEN p.retention_amount ELSE 0 END,
                            p.applied
                        FROM photo AS p
                        JOIN migrated AS m ON m.tenant_id = p.tenant_id
                        WHERE NOT EXISTS (
                            SELECT 1 FROM quotations.quotation_withholdings AS w
                            WHERE w.document_id = p.id AND w.source = p.source);
                    END IF;
                END $$;
                """);

            // Paso 4 (§11.3): la foto de los pedidos, una sola fuente ('Document'). Retenía si con parte propia se contestó
            // que sí, o sin ella si el cliente retenía; aplicaba con la misma regla de la cotización.
            migrationBuilder.Sql(
                $$"""
                DO $$
                BEGIN
                    IF {{Guard}} THEN
                        WITH {{Migrated}},
                        documents AS (
                            SELECT
                                o.id, o.tenant_id, o.subtotal, o.retention_amount, o.customer_with_retention,
                                o.party_with_retention IS TRUE AS party_with_retention,
                                EXISTS (
                                    SELECT 1 FROM quotations.order_parties AS p
                                    WHERE p.order_id = o.id AND p.role = 'Billing') AS has_own_billing,
                                NOT o.bills_to_final_consumer AND o.currency = 'COP' AS applied
                            FROM quotations.orders AS o
                        )
                        INSERT INTO quotations.order_withholdings ({{PhotoColumns}})
                        SELECT
                            d.id, 'Document', 0, m.id, m.name, m.type, m.country, m.percentage, NULL, NULL, NULL,
                            CASE WHEN d.applied THEN d.subtotal ELSE 0 END,
                            CASE WHEN d.applied THEN d.retention_amount ELSE 0 END,
                            d.applied
                        FROM documents AS d
                        JOIN migrated AS m ON m.tenant_id = d.tenant_id
                        WHERE CASE WHEN d.has_own_billing THEN d.party_with_retention ELSE d.customer_with_retention END
                          AND NOT EXISTS (
                              SELECT 1 FROM quotations.order_withholdings AS w
                              WHERE w.document_id = d.id AND w.source = 'Document');
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No hay vuelta: es una migración de datos. Las fotos se van con el Down de AddQuotationWithholdings y
            // AddOrderWithholdings; la tasa del catálogo y sus asignaciones son de otros módulos y se quedan.
        }
    }
}
