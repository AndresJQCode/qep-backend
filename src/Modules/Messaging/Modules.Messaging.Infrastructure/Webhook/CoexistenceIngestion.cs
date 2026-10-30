using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>De dónde viene lo que entra por teléfono en una conexión por coexistencia: el historial de la app (<c>history</c>) o lo
/// que el negocio escribe desde la app (<c>smb_message_echoes</c>).</summary>
internal enum CoexistenceSource
{
    History,
    Echo,
}

/// <summary>
/// Spec 2026-10-29 §4.1: el historial de la app WhatsApp Business entra sin generar trabajo. No pasa por <c>IngestOnceAsync</c>:
/// no suma no leídos, no reabre, no mueve <c>last_inbound_at</c>, no emite eventos en el hilo, no avisa en tiempo real, no encola al
/// bot y no hereda asesor. Una conversación creada por la importación lleva <c>history_managed</c>, y mientras lo lleve la regla de
/// abiertas se reevalúa con cada parte que la toca (las partes llegan desordenadas). Toda sentencia que cambia una conversación que ya
/// existía sube su <c>version</c> (regla R4): lo que un asesor cargó antes de la parte falla por concurrencia en vez de pisarse.
/// </summary>
internal static class CoexistenceIngestion
{
    /// <summary>Un hilo (un teléfono) en una transacción: la conversación, sus mensajes, la foto y la regla.</summary>
    /// <param name="waId">Los dígitos del teléfono, ya normalizados por el parser.</param>
    /// <param name="connectedAt">La hora de la conexión (<c>history_syncs.connected_at</c>); <c>null</c> si no hay fila, y entonces la
    /// conversación importada queda cerrada.</param>
    /// <returns>La conversación, o <c>null</c> si todos los mensajes ya estaban y nada cambió.</returns>
    public static async Task<Guid?> IngestThreadAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, string waId, IReadOnlyList<CoexistenceMessage> messages,
        Guid? customerId, CoexistenceSource source, DateTimeOffset? connectedAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            return null;
        }

        // OrderBy es estable: dos mensajes del mismo segundo conservan el orden en que Meta los mandó.
        var ordered = messages.OrderBy(message => message.OccurredAt).ToList();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var (conversationId, created) = await FindOrCreateAsync(
            dbContext, tenantId, connectionId, waId, customerId, source, ordered[0].OccurredAt, now, cancellationToken);

        var changed = created;
        // La marca durable de lo importado (decisión del owner del 2026-10-30): un eco es tráfico en vivo y no la lleva.
        var imported = source == CoexistenceSource.History;
        CoexistenceMessage? newest = null;
        var newestId = Guid.Empty;
        foreach (var message in ordered)
        {
            var messageId = Guid.CreateVersion7();
            var direction = MessageColumnCodes.ToCode(message.Direction);
            var kind = MessageColumnCodes.ToCode(message.Kind);
            // El estado que escribe la ingesta en vivo: Delivered para un entrante, Sent para un saliente.
            var status = MessageColumnCodes.ToCode(message.Direction == MessageDirection.Outbound ? MessageStatus.Sent : MessageStatus.Delivered);
            var inserted = await dbContext.Database.SqlQuery<Guid>(
                $"""
                INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, caption, details, wamid, reply_to_wamid, reply_to_message_id, created_at, imported)
                VALUES ({messageId}, {conversationId}, {tenantId}, {connectionId}, {message.OccurredAt}, {direction}, {kind}, {status}, {message.Text}, {message.Caption}, {message.DetailsJson}::jsonb, {message.Wamid}, NULL, NULL, {now}, {imported})
                ON CONFLICT (connection_id, wamid) WHERE wamid IS NOT NULL DO NOTHING
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken);
            if (inserted.Count == 0)
            {
                // Review Focus 4: el mensaje ya estaba (en vivo, por eco o por una parte repetida) y no se pisa.
                changed |= await AttachLateMediaAsync(dbContext, connectionId, message, now, cancellationToken);
                continue;
            }

            if (message.Media is { } media)
            {
                await InboundIngestion.InsertMediaAsync(dbContext, messageId, media, now, cancellationToken);
            }

            // Recorrido en orden: el último insertado es el más reciente (y, en empate, el último que Meta mandó, como el `>=` de la
            // ingesta en vivo).
            newest = message;
            newestId = messageId;
            changed = true;
        }

        if (newest is not null)
        {
            await UpdateSummaryAsync(dbContext, conversationId, newest, newestId, customerId, now, cancellationToken);
            if (source == CoexistenceSource.Echo)
            {
                // Regla R11: el negocio respondiendo desde la app es actividad en vivo, y desde ahí una parte del historial que llegue
                // después no le cambia el estado ni los no leídos. Si nació con este eco ya no lleva la marca, así que no escribe.
                await ClearHistoryManagedAsync(dbContext, conversationId, now, cancellationToken);
            }
        }

        if (changed)
        {
            await ApplyOpenRuleAsync(dbContext, conversationId, connectedAt, now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return changed ? conversationId : null;
    }

    /// <summary>Una parte del historial: la fase y el progreso más altos vistos (un valor desconocido, <c>null</c>, nunca pisa uno
    /// conocido, y uno más bajo que llega tarde no lo baja); <c>progress = 100</c> la da por terminada.</summary>
    /// <returns>Las filas tocadas: 0 si la conexión no tiene sincronización.</returns>
    public static Task<int> RecordChunkAsync(
        MessagingDbContext dbContext, Guid connectionId, HistoryChunk chunk, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        // GREATEST ignora los NULL de PostgreSQL: con la fase o el progreso desconocidos se queda el guardado.
        return dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.history_syncs SET
                history_phase    = GREATEST(history_phase, {chunk.Phase}::smallint),
                history_progress = GREATEST(history_progress, {chunk.Progress}::smallint),
                completed_at     = COALESCE(completed_at, CASE WHEN {chunk.Progress}::smallint = 100 THEN {now} END),
                updated_at       = {now},
                version          = version + 1
            WHERE connection_id = {connectionId}
            """, cancellationToken);
    }

    /// <summary>El dueño no aceptó compartir el historial (<c>2593109</c>). La primera vez manda.</summary>
    /// <returns>Las filas tocadas: 0 si la conexión no tiene sincronización.</returns>
    public static Task<int> RecordDeclinedAsync(MessagingDbContext dbContext, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.history_syncs SET
                declined_at = COALESCE(declined_at, {now}),
                updated_at  = {now},
                version     = version + 1
            WHERE connection_id = {connectionId}
            """, cancellationToken);

    /// <summary>Como <c>ConversationOpener.OpenByPhoneAsync</c>: la conversación con BSUID de ese número, si no la legada; si no hay,
    /// una legada nueva. Las dos nacen cerradas. La del historial lleva <c>history_managed</c>, y la regla de abiertas decide después si
    /// queda abierta. La de un eco no lo lleva, porque un eco es tráfico en vivo y no la importación. Nace cerrada porque el negocio
    /// escribiendo desde la app no le deja nada pendiente a un asesor (P-5, decisión del owner del 2026-10-30): abierta y sin asignar
    /// caería en la bandeja y en el triage del embudo. La reabre el contacto cuando escribe en vivo, por el camino de siempre.
    /// <c>Created</c> sólo si esta llamada la insertó.</summary>
    private static async Task<(Guid Id, bool Created)> FindOrCreateAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, string waId, Guid? customerId, CoexistenceSource source,
        DateTimeOffset earliest, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await ConversationOpener.FindByPhoneAsync(dbContext, tenantId, connectionId, waId, cancellationToken) is { } existing)
        {
            return (existing, false);
        }

        var id = Guid.CreateVersion7();
        var fromHistory = source == CoexistenceSource.History;
        var status = nameof(ConversationStatus.Resolved);
        var fromEcho = source == CoexistenceSource.Echo;
        var channel = nameof(ConversationChannel.WhatsApp);
        // Sin BSUID, el blanco del ON CONFLICT es el índice del teléfono de una fila vieja (IX_conversations_connection_wa_legacy).
        var inserted = await dbContext.Database.SqlQuery<Guid>(
            $"""
            INSERT INTO messaging.conversations (id, tenant_id, connection_id, channel, user_id, wa_id, profile_name, customer_id, status, unread_count,
                                                 last_activity_at, history_managed, imported, started_by_echo, created_at, updated_at, version)
            VALUES ({id}, {tenantId}, {connectionId}, {channel}, NULL, {waId}, NULL, {customerId}, {status}, 0, {earliest}, {fromHistory}, {fromHistory}, {fromEcho}, {now}, {now}, 1)
            ON CONFLICT (connection_id, wa_id) WHERE user_id IS NULL DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        if (inserted.Count == 1)
        {
            return (id, true);
        }

        // Otra entrega la creó entre el SELECT y el INSERT; el SELECT va en una sentencia aparte, así ve lo commiteado.
        var raced = await ConversationOpener.FindByPhoneAsync(dbContext, tenantId, connectionId, waId, cancellationToken)
            ?? throw new InvalidOperationException("The conversation that blocked the insert could not be found.");
        return (raced, false);
    }

    private static Task<int> ClearHistoryManagedAsync(
        MessagingDbContext dbContext, Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.conversations SET history_managed = false, updated_at = {now}, version = version + 1
            WHERE id = {conversationId} AND history_managed
            """, cancellationToken);

    /// <summary>La foto de la lista, como <c>IngestEchoAsync</c>: avanza por <c>occurred_at</c> y se queda el más reciente. Nunca toca
    /// <c>last_inbound_*</c>, el asignado ni el asistente. Si la parte sólo trae mensajes más viejos no escribe (regla R4, hallazgo F5):
    /// subir la versión sin cambio visible le daría un 412 al asesor que resuelve o edita en paralelo.</summary>
    private static Task<int> UpdateSummaryAsync(
        MessagingDbContext dbContext, Guid conversationId, CoexistenceMessage newest, Guid newestId, Guid? customerId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var direction = MessageColumnCodes.ToCode(newest.Direction);
        var kind = MessageColumnCodes.ToCode(newest.Kind);
        var status = MessageColumnCodes.ToCode(newest.Direction == MessageDirection.Outbound ? MessageStatus.Sent : MessageStatus.Delivered);
        var preview = Preview(newest);
        return dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.conversations SET
                customer_id            = COALESCE(customer_id, {customerId}::uuid),
                last_activity_at       = GREATEST(last_activity_at, {newest.OccurredAt}),
                last_message_id        = CASE WHEN last_message_at IS NULL OR {newest.OccurredAt} >= last_message_at THEN {newestId} ELSE last_message_id END,
                last_message_direction = CASE WHEN last_message_at IS NULL OR {newest.OccurredAt} >= last_message_at THEN {direction} ELSE last_message_direction END,
                last_message_kind      = CASE WHEN last_message_at IS NULL OR {newest.OccurredAt} >= last_message_at THEN {kind} ELSE last_message_kind END,
                last_message_preview   = CASE WHEN last_message_at IS NULL OR {newest.OccurredAt} >= last_message_at THEN {preview} ELSE last_message_preview END,
                last_message_status    = CASE WHEN last_message_at IS NULL OR {newest.OccurredAt} >= last_message_at THEN {status} ELSE last_message_status END,
                last_message_at        = GREATEST(last_message_at, {newest.OccurredAt}),
                updated_at             = {now},
                version                = version + 1
            WHERE id = {conversationId}
              AND (last_message_at IS NULL
                   OR {newest.OccurredAt} >= last_message_at
                   OR last_activity_at IS NULL OR {newest.OccurredAt} > last_activity_at
                   OR (customer_id IS NULL AND {customerId}::uuid IS NOT NULL))
            """, cancellationToken);
    }

    /// <summary>
    /// §4.1 y D-6, sólo mientras <c>history_managed</c>: abierta si el último mensaje de todo lo importado es del contacto y no es
    /// anterior a la conexión menos 24 h, con no leídos = los mensajes del contacto desde el último del negocio; si no, cerrada y
    /// en 0. «Desde» incluye el mismo instante (regla R10): el timestamp de Meta es de segundos, y si el último de todos es del contacto,
    /// uno suyo del mismo segundo que el saliente vino después. Sin hora de conexión, cerrada. Una conversación con asistente no se
    /// toca: cerrarla rompería <c>CK_conversations_automation_resolved</c>, y si tiene asistente ya la tomó el flujo en vivo. Si nada
    /// cambia no se escribe, así que la versión sólo sube cuando la regla movió algo.
    /// </summary>
    private static Task<int> ApplyOpenRuleAsync(
        MessagingDbContext dbContext, Guid conversationId, DateTimeOffset? connectedAt, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            WITH rule AS (
                SELECT c.id,
                       ({connectedAt}::timestamptz IS NOT NULL
                        AND c.last_message_direction = 1
                        AND c.last_message_at >= {connectedAt}::timestamptz - interval '24 hours') AS is_open
                  FROM messaging.conversations c
                 WHERE c.id = {conversationId} AND c.history_managed AND c.automation_status = 'None'),
            target AS (
                SELECT rule.id,
                       CASE WHEN rule.is_open THEN 'Open' ELSE 'Resolved' END AS new_status,
                       CASE WHEN rule.is_open THEN (
                           SELECT count(*) FROM messaging.messages m
                            WHERE m.conversation_id = rule.id AND m.direction = 1
                              AND m.occurred_at >= COALESCE(
                                  (SELECT max(o.occurred_at) FROM messaging.messages o WHERE o.conversation_id = rule.id AND o.direction = 2),
                                  '-infinity'::timestamptz))::int
                       ELSE 0 END AS new_unread
                  FROM rule)
            UPDATE messaging.conversations c SET
                status               = target.new_status,
                resolution_reason_id = NULL,
                unread_count         = target.new_unread,
                updated_at           = {now},
                version              = c.version + 1
              FROM target
             WHERE c.id = target.id
               AND c.history_managed
               AND (c.status <> target.new_status OR c.unread_count <> target.new_unread OR c.resolution_reason_id IS NOT NULL)
            """, cancellationToken);

    /// <summary>
    /// P-6: el archivo de un <c>media_placeholder</c> que llega después con el mismo <c>wamid</c> (V-2: supuesto, la forma real del
    /// webhook está sin verificar). Sólo si el guardado sigue siendo el placeholder y no tiene medio: entonces toma el <c>kind</c> y la
    /// leyenda del que llegó, pierde la marca y queda encolado para que <c>MediaCopyWorker</c> lo baje. El <c>UPDATE</c> toma la fila:
    /// dos partes simultáneas con el mismo wamid no encolan dos veces. Sube la <c>version</c> de la conversación (regla R4).
    /// </summary>
    /// <returns><c>true</c> si cambió el mensaje guardado.</returns>
    private static async Task<bool> AttachLateMediaAsync(
        MessagingDbContext dbContext, Guid connectionId, CoexistenceMessage message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (message.Media is not { } media || message.IsMediaPlaceholder)
        {
            return false;
        }

        var kind = MessageColumnCodes.ToCode(message.Kind);
        var attached = await dbContext.Database.SqlQuery<Guid>(
            $"""
            WITH upd AS (
                UPDATE messaging.messages m SET
                    kind    = {kind},
                    caption = COALESCE(m.caption, {message.Caption}),
                    details = NULLIF(m.details - 'historyMedia', jsonb_build_object())
                 WHERE m.connection_id = {connectionId} AND m.wamid = {message.Wamid}
                   AND m.details ->> 'historyMedia' = 'placeholder'
                   AND NOT EXISTS (SELECT 1 FROM messaging.message_media mm WHERE mm.message_id = m.id)
                RETURNING m.id, m.conversation_id),
            conv AS (
                UPDATE messaging.conversations c SET
                    last_message_kind = CASE WHEN c.last_message_id = upd.id THEN {kind} ELSE c.last_message_kind END,
                    updated_at        = {now},
                    version           = c.version + 1
                  FROM upd
                 WHERE c.id = upd.conversation_id
                RETURNING c.id)
            SELECT upd.id AS "Value" FROM upd
            """).ToListAsync(cancellationToken);
        if (attached.Count == 0)
        {
            return false;
        }

        await InboundIngestion.InsertMediaAsync(dbContext, attached[0], media, now, cancellationToken);
        return true;
    }

    /// <summary>El texto, o la leyenda de un medio, recortado al ancho de la columna, como <see cref="InboundIngestion.Preview"/>.</summary>
    private static string? Preview(CoexistenceMessage message)
    {
        var source = message.Text ?? message.Caption;
        return source is null ? null
            : source.Length <= InboundIngestion.PreviewMaxLength ? source : source[..InboundIngestion.PreviewMaxLength];
    }
}
