using BuildingBlocks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Automations.Infrastructure.Persistence;
using Modules.Customers.Infrastructure.Persistence;
using Modules.Messaging.Domain;

namespace Bootstrapper;

/// <summary>
/// Adapta Quotations, Loans, ServiceContracts, Messaging, Automations, Pipeline, Pos, Geography y Shopify al puerto
/// <see cref="ICustomerDeletionReferences"/> de Customers (spec 2026-11-10, borrar clientes, §4.2, §4.3 y §5). Vive en el
/// composition root por la misma razón que <see cref="CustomerMergeReferences"/>: Customers no referencia los otros módulos
/// (<c>CustomersLayerTests</c>) y el acoplamiento entre esquemas se cablea acá.
///
/// **Corre sobre el <see cref="CustomersDbContext"/> del scope y exige su transacción.** Así cada sentencia usa la conexión del
/// borrado y queda dentro de su transacción: si una falla, se deshace todo el lote, Customers incluido. Sin transacción, el
/// candado consultivo se soltaría al terminar la sentencia y cada <c>DELETE</c> confirmaría solo, así que se rechaza con
/// <see cref="InvalidOperationException"/> en vez de borrar a medias.
///
/// Cada sentencia filtra por <c>tenant_id</c> y por <c>&lt;col&gt; = ANY(ids)</c> (o por las conversaciones de esos clientes), con
/// todo valor como parámetro, y va **por lote**: el número de sentencias no depende de cuántos clientes, conversaciones o
/// mensajes haya (§5, rendimiento).
///
/// **Lo que lee por nombre.** <c>quotations.quotations.client_id</c>, <c>quotations.orders.client_id</c>,
/// <c>loans.loans.customer_id</c>, <c>billing.services.customer_id</c>, <c>billing.charges.customer_id</c>,
/// <c>billing.customer_profiles.customer_id</c>; en <c>messaging</c>: <c>conversations</c> (<c>id</c>, <c>customer_id</c>,
/// <c>blocked_at</c>), <c>messages</c> (<c>conversation_id</c>, <c>direction</c>), <c>message_media.storage_key</c>,
/// <c>scheduled_messages.media_storage_key</c>, <c>automation_inbox.conversation_id</c>, <c>campaign_recipients</c>
/// (<c>customer_id</c>, <c>conversation_id</c>, <c>message_id</c>, <c>status</c>, <c>failure_code</c>, <c>failure_reason</c>) y
/// <c>campaigns</c> (<c>audience_customer_ids</c>, <c>audience_tag_ids</c>, <c>status</c>, <c>version</c>); en
/// <c>automations</c>: <c>conversation_runs</c>, <c>run_units.run_id</c> y <c>http_calls.run_id</c>; en <c>pipeline</c>:
/// <c>deals</c> (<c>customer_id</c>, <c>origin_conversation_id</c>), <c>deal_stage_history</c>, <c>deal_quotations</c>,
/// <c>deal_orders</c> y <c>triage_decisions</c> (<c>deal_id</c>, <c>customer_id</c>, <c>conversation_id</c>);
/// <c>pos.sales.customer_id</c>, <c>geography.unresolved_city_occurrences.customer_id</c> y
/// <c>shopify.orders.qep_customer_id</c>.
///
/// **El costo de este camino.** Renombrar una de esas columnas o tablas compila y rompe el borrado en ejecución con
/// <c>42703 undefined_column</c> o <c>42P01 undefined_table</c>: responde 500 y, por la transacción, no escribe nada. Lo amarra
/// <c>CustomerDeletionAcrossModulesTests</c>, que resuelve este adaptador del contenedor y lo corre contra el esquema migrado
/// de todos los módulos.
/// </summary>
internal sealed class CustomerDeletionReferences(CustomersDbContext customers) : ICustomerDeletionReferences
{
    // CK_messages_event_shape: dirección 3 es un evento de la conversación (asignada, resuelta…), no un mensaje que alguien
    // escribió. La auditoría cuenta mensajes, así que los eventos no entran.
    private const int EventDirection = 3;

    public async Task LockAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var customerIds = Sorted(ids);
        if (customerIds.Length == 0)
        {
            return;
        }

        EnsureTransaction();
        // Un candado por cliente y en orden de id: dos borrados con clientes en común los piden en el mismo orden y no se cruzan.
        // Son sentencias por cliente a propósito (como mucho 100): un solo SELECT sobre unnest no garantiza en qué orden Postgres
        // evalúa las llamadas.
        await CustomersWriteRaces.RunAsync(async () =>
        {
            foreach (var id in customerIds)
            {
                await customers.Database.ExecuteSqlAsync(CustomerReferenceLock.Exclusive(tenantId, id), cancellationToken);
            }
        });
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> FindBlockersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var customerIds = Sorted(ids);
        if (customerIds.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<string>>();
        }

        EnsureTransaction();
        return await CustomersWriteRaces.RunAsync(() => FindBlockersCoreAsync(tenantId, customerIds, cancellationToken));
    }

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> FindBlockersCoreAsync(
        Guid tenantId, Guid[] ids, CancellationToken cancellationToken)
    {
        var database = customers.Database;
        var found = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal)
        {
            // §4.2: cualquier fila, en cualquier estado (borrador, anulado, cancelado), bloquea.
            [CustomerDeletionBlockReasons.Quotations] = [.. await database.SqlQuery<Guid>($"""
                SELECT DISTINCT client_id AS "Value" FROM quotations.quotations WHERE tenant_id = {tenantId} AND client_id = ANY({ids})
                """).ToListAsync(cancellationToken)],
            [CustomerDeletionBlockReasons.Orders] = [.. await database.SqlQuery<Guid>($"""
                SELECT DISTINCT client_id AS "Value" FROM quotations.orders WHERE tenant_id = {tenantId} AND client_id = ANY({ids})
                """).ToListAsync(cancellationToken)],
            [CustomerDeletionBlockReasons.Loans] = [.. await database.SqlQuery<Guid>($"""
                SELECT DISTINCT customer_id AS "Value" FROM loans.loans WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
                """).ToListAsync(cancellationToken)],
            // Los cobros cuelgan de un servicio, pero se miran también: la fusión los mueve por separado y no hay FK que asegure
            // que el cobro y su servicio sean del mismo cliente.
            [CustomerDeletionBlockReasons.ServiceContracts] = [.. await database.SqlQuery<Guid>($"""
                SELECT customer_id AS "Value" FROM billing.services WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
                UNION
                SELECT customer_id AS "Value" FROM billing.charges WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
                """).ToListAsync(cancellationToken)],
        };

        // Antes del FOR UPDATE: el candado de conversación del runner del chatbot, de todas las conversaciones de estos clientes. El runner
        // lo toma (sin esperar) antes de su corrida y, con los dos, escribe la conversación por otra conexión de Mensajería: si el borrado
        // bloqueara la conversación primero, esa escritura esperaría al borrado, el borrado a la corrida y la corrida a la escritura, un
        // ciclo que Postgres no ve (la transacción del runner queda ociosa) y que sólo corta el timeout. Así el borrado espera al runner sin
        // tener ninguna fila suya, y un runner que llega después encuentra el candado tomado y deja la unidad para luego. El conjunto no
        // cambia: quien cuelga una conversación de uno de estos clientes toma su candado (compartido: las ingestas del webhook, el
        // opener, el cambio de cliente y el LinkCustomerAsync del chatbot; exclusivo: la fusión), y LockAsync ya tiene el exclusivo.
        await database.ExecuteSqlAsync($"""
            SELECT pg_advisory_xact_lock({AutomationsConversationLock.Namespace}, hashtext(conversation.id::text))
            FROM (SELECT id FROM messaging.conversations WHERE tenant_id = {tenantId} AND customer_id = ANY({ids}) ORDER BY id) AS conversation
            """, cancellationToken);

        // D-9 y P5: FOR UPDATE sobre todas las conversaciones de estos clientes, bloqueadas o no. Hasta el commit nadie las bloquea
        // (lo que convertiría en bloqueado a un cliente ya decidido como borrable) ni las desbloquea; y el borrado de más abajo
        // borra exactamente las que se leyeron acá.
        var conversations = await database.SqlQuery<ConversationBlockRow>($"""
            SELECT customer_id AS "CustomerId", blocked_at IS NOT NULL AS "Blocked"
            FROM messaging.conversations
            WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
            FOR UPDATE
            """).ToListAsync(cancellationToken);
        found[CustomerDeletionBlockReasons.BlockedContact] = [.. conversations.Where(row => row.Blocked).Select(row => row.CustomerId)];

        var blockers = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var id in ids)
        {
            var reasons = CustomerDeletionBlockReasons.Ordered.Where(reason => found[reason].Contains(id)).ToArray();
            if (reasons.Length > 0)
            {
                blockers[id] = reasons;
            }
        }

        return blockers;
    }

    public async Task<CustomerDeletionOutcome> RemoveAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var customerIds = Sorted(ids);
        if (customerIds.Length == 0)
        {
            return new CustomerDeletionOutcome(
                new Dictionary<Guid, CustomerDeletionCounts>(), new Dictionary<Guid, IReadOnlyList<string>>());
        }

        EnsureTransaction();
        return await CustomersWriteRaces.RunAsync(() => RemoveCoreAsync(tenantId, customerIds, cancellationToken));
    }

    private async Task<CustomerDeletionOutcome> RemoveCoreAsync(Guid tenantId, Guid[] ids, CancellationToken cancellationToken)
    {
        var database = customers.Database;

        // Las conversaciones de estos clientes. FindBlockersAsync ya las tomó con FOR UPDATE y quien abre una conversación toma el
        // candado del cliente (A-12), así que es la misma lista; el FOR UPDATE de acá sólo lo hace explícito.
        var conversationRows = await database.SqlQuery<ConversationRow>($"""
            SELECT id AS "Id", customer_id AS "CustomerId"
            FROM messaging.conversations
            WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
            FOR UPDATE
            """).ToListAsync(cancellationToken);
        var conv = conversationRows.Select(row => row.Id).ToArray();

        // P12, A-9: las claves de R2 se leen antes de borrar las conversaciones; el CASCADE se lleva las filas que las guardan.
        // message_media no tiene tenant_id: el tenant lo da la conversación.
        var keyRows = await database.SqlQuery<StorageKeyRow>($"""
            SELECT c.customer_id AS "CustomerId", media.storage_key AS "StorageKey"
            FROM messaging.message_media media
            JOIN messaging.messages m ON m.id = media.message_id
            JOIN messaging.conversations c ON c.id = m.conversation_id
            WHERE c.tenant_id = {tenantId} AND c.id = ANY({conv}) AND media.storage_key IS NOT NULL
            UNION
            SELECT c.customer_id AS "CustomerId", scheduled.media_storage_key AS "StorageKey"
            FROM messaging.scheduled_messages scheduled
            JOIN messaging.conversations c ON c.id = scheduled.conversation_id
            WHERE c.tenant_id = {tenantId} AND c.id = ANY({conv}) AND scheduled.media_storage_key IS NOT NULL
            """).ToListAsync(cancellationToken);

        // A-8: lo que se lleva cada cliente, para la auditoría. Antes de tocar nada: después ya no hay qué contar.
        var countRows = await database.SqlQuery<CountsRow>($"""
            SELECT
                requested.id AS "CustomerId",
                (SELECT count(*)::int
                   FROM messaging.messages m JOIN messaging.conversations c ON c.id = m.conversation_id
                  WHERE c.tenant_id = {tenantId} AND c.customer_id = requested.id AND m.direction <> {EventDirection}) AS "Messages",
                (SELECT count(*)::int FROM pipeline.deals
                  WHERE tenant_id = {tenantId} AND customer_id = requested.id) AS "Deals",
                (SELECT count(*)::int FROM pos.sales
                  WHERE tenant_id = {tenantId} AND customer_id = requested.id) AS "PosSales",
                (SELECT count(*)::int FROM messaging.campaign_recipients
                  WHERE tenant_id = {tenantId} AND customer_id = requested.id) AS "CampaignRecipients"
            FROM unnest({ids}) AS requested (id)
            """).ToListAsync(cancellationToken);

        // Las corridas del chatbot (y lo que cuelga de ellas) de estas conversaciones. Sin FK hacia Messaging: van a mano.
        await database.ExecuteSqlAsync($"""
            DELETE FROM automations.http_calls
            WHERE tenant_id = {tenantId}
              AND run_id IN (SELECT id FROM automations.conversation_runs WHERE tenant_id = {tenantId} AND conversation_id = ANY({conv}))
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            DELETE FROM automations.run_units
            WHERE tenant_id = {tenantId}
              AND run_id IN (SELECT id FROM automations.conversation_runs WHERE tenant_id = {tenantId} AND conversation_id = ANY({conv}))
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            DELETE FROM automations.conversation_runs WHERE tenant_id = {tenantId} AND conversation_id = ANY({conv})
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            DELETE FROM messaging.automation_inbox WHERE tenant_id = {tenantId} AND conversation_id = ANY({conv})
            """, cancellationToken);

        // A-10 y P10: los destinatarios se conservan (las estadísticas de la campaña se cuentan en vivo de esta tabla). Primero el
        // pendiente pasa a omitido, como uno sin ficha: así el despachador nunca lo toma. Después se sueltan el cliente y, si era
        // de una conversación que se borra, la conversación y su mensaje. Un message_id sin conversación nunca llegó a una: también
        // se suelta.
        var unreachable = CampaignFailureCodes.Unreachable;
        var unreachableReason = CampaignFailureReasons.For(unreachable);
        await database.ExecuteSqlAsync($"""
            UPDATE messaging.campaign_recipients
               SET status = 'Skipped', failure_code = {unreachable}, failure_reason = {unreachableReason}, locked_until = NULL
             WHERE tenant_id = {tenantId} AND customer_id = ANY({ids}) AND status = 'Pending'
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            UPDATE messaging.campaign_recipients
               SET message_id = CASE
                       WHEN conversation_id = ANY({conv}) OR (customer_id = ANY({ids}) AND conversation_id IS NULL) THEN NULL
                       ELSE message_id END,
                   conversation_id = CASE WHEN conversation_id = ANY({conv}) THEN NULL ELSE conversation_id END,
                   customer_id = CASE WHEN customer_id = ANY({ids}) THEN NULL ELSE customer_id END
             WHERE tenant_id = {tenantId} AND (customer_id = ANY({ids}) OR conversation_id = ANY({conv}))
            """, cancellationToken);

        // P11: sólo las que todavía no congelaron su audiencia, conservando el orden. Si el cliente era lo único de la audiencia
        // (sin etiquetas), el id se queda: CK_campaigns_audience no admite una audiencia vacía, y al congelarla el despachador
        // ya no encuentra la ficha. La versión sube, como en la fusión: quien la tenga abierta recibe 412 al guardar la vieja.
        await database.ExecuteSqlAsync($"""
            UPDATE messaging.campaigns
               SET audience_customer_ids = ARRAY(
                     SELECT audience.id
                     FROM unnest(audience_customer_ids) WITH ORDINALITY AS audience (id, position)
                     WHERE audience.id <> ALL({ids})
                     ORDER BY audience.position),
                   version = version + 1
             WHERE tenant_id = {tenantId} AND status IN ('Draft', 'Scheduled') AND audience_customer_ids && {ids}
               AND (cardinality(audience_tag_ids) > 0
                    OR EXISTS (SELECT 1 FROM unnest(audience_customer_ids) AS kept (id) WHERE kept.id <> ALL({ids})))
            """, cancellationToken);

        // D-3: los negocios del cliente se borran con su historial, sus fotos y sus decisiones de triage. La decisión puede colgar
        // del cliente, de una de sus conversaciones o de uno de sus negocios.
        await database.ExecuteSqlAsync($"""
            DELETE FROM pipeline.deal_stage_history
            WHERE tenant_id = {tenantId}
              AND deal_id IN (SELECT id FROM pipeline.deals WHERE tenant_id = {tenantId} AND customer_id = ANY({ids}))
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            DELETE FROM pipeline.deal_quotations
            WHERE tenant_id = {tenantId}
              AND deal_id IN (SELECT id FROM pipeline.deals WHERE tenant_id = {tenantId} AND customer_id = ANY({ids}))
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            DELETE FROM pipeline.deal_orders
            WHERE tenant_id = {tenantId}
              AND deal_id IN (SELECT id FROM pipeline.deals WHERE tenant_id = {tenantId} AND customer_id = ANY({ids}))
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            DELETE FROM pipeline.triage_decisions
            WHERE tenant_id = {tenantId}
              AND (customer_id = ANY({ids})
                   OR conversation_id = ANY({conv})
                   OR deal_id IN (SELECT id FROM pipeline.deals WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})))
            """, cancellationToken);
        // §8: el negocio de otro cliente que nació de una de estas conversaciones (la conversación cambió de cliente después)
        // sobrevive; su origen se suelta (A-7) en vez de quedar apuntando a una conversación que ya no existe.
        await database.ExecuteSqlAsync($"""
            UPDATE pipeline.deals SET origin_conversation_id = NULL
            WHERE tenant_id = {tenantId} AND origin_conversation_id = ANY({conv}) AND customer_id <> ALL({ids})
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            DELETE FROM pipeline.deals WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
            """, cancellationToken);

        // D-5: los CASCADE se llevan messages, message_media y scheduled_messages.
        await database.ExecuteSqlAsync($"""
            DELETE FROM messaging.conversations WHERE tenant_id = {tenantId} AND id = ANY({conv})
            """, cancellationToken);

        // A-4: la venta conserva la foto del cliente (nombre y documento); sólo se suelta el id.
        await database.ExecuteSqlAsync($"""
            UPDATE pos.sales SET customer_id = NULL WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
            """, cancellationToken);
        // Un perfil de cobro sin servicios (con servicios, el cliente ya estaba bloqueado).
        await database.ExecuteSqlAsync($"""
            DELETE FROM billing.customer_profiles WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
            """, cancellationToken);
        await database.ExecuteSqlAsync($"""
            DELETE FROM geography.unresolved_city_occurrences WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
            """, cancellationToken);

        // Última a propósito: la prueba de atomicidad rompe esta sentencia, como en la fusión.
        await database.ExecuteSqlAsync($"""
            UPDATE shopify.orders SET qep_customer_id = NULL WHERE tenant_id = {tenantId} AND qep_customer_id = ANY({ids})
            """, cancellationToken);

        var conversationsByCustomer = conversationRows
            .GroupBy(row => row.CustomerId)
            .ToDictionary(group => group.Key, group => group.Count());
        var counts = countRows.ToDictionary(
            row => row.CustomerId,
            row => new CustomerDeletionCounts(
                conversationsByCustomer.GetValueOrDefault(row.CustomerId), row.Messages, row.Deals, row.PosSales, row.CampaignRecipients));
        var storageKeys = keyRows
            .GroupBy(row => row.CustomerId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(row => row.StorageKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        return new CustomerDeletionOutcome(counts, storageKeys);
    }

    private void EnsureTransaction()
    {
        if (customers.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Deleting customers requires the transaction of the CustomersDbContext: without it the reference locks are released "
                + "after each statement and a failure would leave the batch half deleted.");
        }
    }

    private static Guid[] Sorted(IReadOnlyCollection<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return [.. ids.Distinct().Order()];
    }

    private sealed class ConversationBlockRow
    {
        public Guid CustomerId { get; init; }

        public bool Blocked { get; init; }
    }

    private sealed class ConversationRow
    {
        public Guid Id { get; init; }

        public Guid CustomerId { get; init; }
    }

    private sealed class StorageKeyRow
    {
        public Guid CustomerId { get; init; }

        public string StorageKey { get; init; } = string.Empty;
    }

    private sealed class CountsRow
    {
        public Guid CustomerId { get; init; }

        public int Messages { get; init; }

        public int Deals { get; init; }

        public int PosSales { get; init; }

        public int CampaignRecipients { get; init; }
    }
}
