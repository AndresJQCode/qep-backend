using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Adapta Quotations, Pos, Pipeline, Messaging y Shopify al puerto <see cref="ICustomerMergeReferences"/> de Customers
/// (spec 2026-10-24 §4). Vive en el composition root por la misma razón que <see cref="CatalogVariantUsageChecker"/>:
/// Customers no referencia los otros módulos (<c>CustomersLayerTests</c>) y el acoplamiento entre esquemas se cablea acá.
///
/// **Corre sobre el <see cref="CustomersDbContext"/> del scope, no sobre el de cada módulo.** Así cada <c>UPDATE</c> usa la
/// misma conexión que la fusión y, con la transacción explícita abierta, queda dentro de ella: si uno falla, se deshace
/// también lo de Customers (la fusión no se puede deshacer, así que queda completa o no pasa). Con otro contexto iría por
/// otra conexión y la palabra «transacción» no significaría nada.
///
/// Cada sentencia filtra por <c>tenant_id</c> y por <c>&lt;col&gt; = ANY(ids)</c>, con todo valor como parámetro. Ninguna
/// sube <c>version</c> salvo la de campañas (F15 y ruling C1): una conversación o un negocio abiertos en otra pestaña no
/// deben dar 412 por la fusión; una campaña en edición sí, porque su audiencia cambió. <c>messaging.campaign_recipients</c>
/// no se toca: es la foto de un envío y su índice único <c>(campaign_id, customer_id)</c> chocaría. La foto del nombre de
/// <c>pos.sales</c> tampoco: es lo que se cobró.
///
/// **El costo de este camino (§4).** La fusión conoce por nombre las tablas y columnas de cinco módulos. Renombrar una de
/// ellas compila y rompe la fusión en ejecución con <c>42703 undefined_column</c> o <c>42P01 undefined_table</c>. Lo amarran
/// <c>CustomerMergeReferencesTests</c> (réplicas mínimas, que no siguen solas a las migraciones) y
/// <c>CustomerMergeAcrossModulesApiTests.TheRealAdapterRunsAgainstTheMigratedSchema</c>, que resuelve este adaptador del
/// contenedor y lo corre contra el esquema migrado de todos los módulos.
/// </summary>
internal sealed class CustomerMergeReferences(CustomersDbContext customers) : ICustomerMergeReferences
{
    public async Task MoveAsync(
        Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mergedIds);
        if (mergedIds.Count == 0)
        {
            return;
        }

        // Un deadlock o una falla de serialización con otra escritura de estas tablas es 412, no 500 (CustomersWriteRaces): este
        // SQL no pasa por el unit of work de Customers, así que la traducción va acá.
        await CustomersWriteRaces.RunAsync(() => MoveCoreAsync(tenantId, survivorId, mergedIds.Distinct().ToArray(), cancellationToken));
    }

    private async Task MoveCoreAsync(Guid tenantId, Guid survivorId, Guid[] ids, CancellationToken cancellationToken)
    {
        var database = customers.Database;

        // Orden fijo, Shopify último: la prueba de atomicidad de FC-09 rompe la última sentencia.
        await database.ExecuteSqlAsync($"""
            UPDATE quotations.quotations SET client_id = {survivorId} WHERE tenant_id = {tenantId} AND client_id = ANY({ids})
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            UPDATE quotations.orders SET client_id = {survivorId} WHERE tenant_id = {tenantId} AND client_id = ANY({ids})
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            UPDATE pos.sales SET customer_id = {survivorId} WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            UPDATE pipeline.deals SET customer_id = {survivorId} WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            UPDATE pipeline.deal_quotations SET customer_id = {survivorId}
            WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            UPDATE pipeline.deal_orders SET customer_id = {survivorId} WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            UPDATE pipeline.triage_decisions SET customer_id = {survivorId}
            WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            UPDATE messaging.conversations SET customer_id = {survivorId}
            WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
            """, cancellationToken);

        // §4: sólo las que todavía no congelaron su audiencia. Quitar repetidos deja al que queda en su primera aparición,
        // así que el orden de la audiencia se conserva; nunca crece, así que CK_campaigns_customers_cap sigue valiendo. La
        // versión sube (ruling C1): quien tenga la campaña abierta recibe 412 al guardar la audiencia vieja.
        await database.ExecuteSqlAsync($"""
            UPDATE messaging.campaigns
               SET audience_customer_ids = ARRAY(
                     SELECT swapped.id
                     FROM (
                         SELECT CASE WHEN audience.id = ANY({ids}) THEN {survivorId} ELSE audience.id END AS id, audience.position
                         FROM unnest(audience_customer_ids) WITH ORDINALITY AS audience (id, position)) AS swapped
                     GROUP BY swapped.id
                     ORDER BY min(swapped.position)),
                   version = version + 1
             WHERE tenant_id = {tenantId} AND status IN ('Draft', 'Scheduled') AND audience_customer_ids && {ids}
            """, cancellationToken);

        // Cobros recurrentes (spec 2026-10-29 §3.1): servicios y cobros pasan al que queda, sin subir versión (F15). Antes de
        // Shopify, que sigue siendo la última sentencia.
        await database.ExecuteSqlAsync($"""
            UPDATE billing.services SET customer_id = {survivorId} WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            UPDATE billing.charges SET customer_id = {survivorId} WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
            """, cancellationToken);

        // El perfil es uno por cliente (PK (tenant, cliente)): si el que queda ya tiene uno, el del fusionado sobra; si no, el
        // más reciente de los fusionados se reasigna y los demás se borran.
        await database.ExecuteSqlAsync($"""
            DELETE FROM billing.customer_profiles
            WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
              AND EXISTS (SELECT 1 FROM billing.customer_profiles survivor
                          WHERE survivor.tenant_id = {tenantId} AND survivor.customer_id = {survivorId})
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            DELETE FROM billing.customer_profiles older
            WHERE older.tenant_id = {tenantId} AND older.customer_id = ANY({ids})
              AND EXISTS (SELECT 1 FROM billing.customer_profiles newer
                          WHERE newer.tenant_id = {tenantId} AND newer.customer_id = ANY({ids})
                            AND (newer.updated_at, newer.customer_id) > (older.updated_at, older.customer_id))
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            UPDATE billing.customer_profiles SET customer_id = {survivorId}
            WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
            """, cancellationToken);

        await database.ExecuteSqlAsync($"""
            UPDATE shopify.orders SET qep_customer_id = {survivorId}
            WHERE tenant_id = {tenantId} AND qep_customer_id = ANY({ids})
            """, cancellationToken);
    }

    // Seguimiento (spec cobros-recurrentes §3.1): los servicios y cobros de Billing no entran en CustomerMergeReferenceCounts
    // porque sumar una columna cambiaría la vista previa de la fusión en el frontend. Sin cambios en la fase 1.
    public async Task<CustomerMergeReferenceCounts> CountAsync(
        Guid tenantId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mergedIds);
        if (mergedIds.Count == 0)
        {
            return new CustomerMergeReferenceCounts(0, 0, 0, 0, 0);
        }

        var ids = mergedIds.Distinct().ToArray();
        var rows = await customers.Database.SqlQuery<CountsRow>($"""
            SELECT
                (SELECT count(*)::int FROM quotations.quotations WHERE tenant_id = {tenantId} AND client_id = ANY({ids})) AS "Quotations",
                (SELECT count(*)::int FROM quotations.orders WHERE tenant_id = {tenantId} AND client_id = ANY({ids})) AS "Orders",
                (SELECT count(*)::int FROM messaging.conversations WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})) AS "Conversations",
                (SELECT count(*)::int FROM pos.sales WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})) AS "PosSales",
                (SELECT count(*)::int FROM pipeline.deals WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})) AS "Deals"
            """).ToListAsync(cancellationToken);

        var row = rows.Single();
        return new CustomerMergeReferenceCounts(row.Quotations, row.Orders, row.Conversations, row.PosSales, row.Deals);
    }

    private sealed class CountsRow
    {
        public int Quotations { get; init; }

        public int Orders { get; init; }

        public int Conversations { get; init; }

        public int PosSales { get; init; }

        public int Deals { get; init; }
    }
}
