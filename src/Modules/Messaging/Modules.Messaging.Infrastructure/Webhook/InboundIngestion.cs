using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Npgsql;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Spec 2026-10-09 §7.5, literal: tres sentencias en una transacción, sin pasar por el agregado,
/// para que dos pods sobre la misma conversación sumen bien sin reintentos.</summary>
internal static class InboundIngestion
{
    public const int PreviewMaxLength = Conversation.PreviewMaxLength;

    /// <summary>§9.5 (P5): la adopción de una fila vieja choca con una conversación que otro pod creó con el mismo BSUID
    /// entre el SELECT y el UPDATE. Se reintenta una vez: la segunda vuelta encuentra la nueva por BSUID.</summary>
    private const string ConnectionUserIndex = "IX_conversations_connection_user";

    /// <returns>El id del mensaje insertado, o <c>null</c> si Meta lo reenvió (ya estaba).</returns>
    public static async Task<Guid?> IngestAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, InboundMessage message, InboundContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await IngestOnceAsync(dbContext, tenantId, connectionId, message, context, now, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation && exception.ConstraintName == ConnectionUserIndex)
        {
            return await IngestOnceAsync(dbContext, tenantId, connectionId, message, context, now, cancellationToken);
        }
    }

    private static async Task<Guid?> IngestOnceAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, InboundMessage message, InboundContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // 1. La conversación (spec 2026-10-10 §8.1).
        var conversation = await ResolveConversationAsync(dbContext, tenantId, connectionId, message, context, now, cancellationToken);
        var conversationId = conversation.Id;

        // 2. El mensaje; un reenvío no inserta nada. reply_to_wamid guarda la cita tal cual y reply_to_message_id la
        // resuelve contra un mensaje de la misma conexión (IX_messages_connection_wamid); sin cita, o si no está, NULL.
        var messageId = Guid.CreateVersion7();
        var kind = MessageColumnCodes.ToCode(message.Kind);
        var insertedMessage = await dbContext.Database.SqlQuery<Guid>(
            $"""
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, caption, details, wamid, reply_to_wamid, reply_to_message_id, created_at)
            VALUES ({messageId}, {conversationId}, {tenantId}, {connectionId}, {message.OccurredAt}, 1, {kind}, 2, {message.Text}, {message.Caption}, {message.DetailsJson}::jsonb, {message.Wamid}, {message.QuotedWamid},
                    (SELECT id FROM messaging.messages WHERE connection_id = {connectionId} AND wamid = {message.QuotedWamid}), {now})
            ON CONFLICT (connection_id, wamid) WHERE wamid IS NOT NULL DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        if (insertedMessage.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        // 3. Contadores, reapertura, ventana, foto y cliente. Las expresiones del SET leen los valores viejos; el CTE
        // bloquea la fila y devuelve el estado y el cliente de antes, que deciden los eventos. FOR NO KEY UPDATE y no
        // FOR UPDATE: el INSERT de la sentencia 2 ya tiene FOR KEY SHARE sobre la fila (la FK), y FOR UPDATE choca con
        // ese candado; dos entregas de la misma conversación quedaban en deadlock (WebhookLoadTests lo mostró).
        var preview = Preview(message);
        var before = (await dbContext.Database.SqlQuery<PreviousState>(
            $"""
            WITH old AS (
                SELECT status AS old_status, customer_id AS old_customer_id
                FROM messaging.conversations WHERE id = {conversationId} FOR NO KEY UPDATE)
            UPDATE messaging.conversations SET
                unread_count       = unread_count + 1,
                status             = 'Open',
                profile_name       = COALESCE({message.ProfileName}, profile_name),
                wa_id              = COALESCE({message.WaId}, wa_id),
                username           = COALESCE({message.Username}, username),
                parent_user_id     = COALESCE({message.ParentUserId}, parent_user_id),
                customer_id        = COALESCE(customer_id, {context.CustomerId}),
                last_inbound_wamid = CASE WHEN last_inbound_at IS NULL OR {message.OccurredAt} >= last_inbound_at THEN {message.Wamid} ELSE last_inbound_wamid END,
                last_inbound_at    = GREATEST(last_inbound_at, {message.OccurredAt}),
                last_activity_at   = GREATEST(last_activity_at, {message.OccurredAt}),
                last_message_id        = CASE WHEN last_message_at IS NULL OR {message.OccurredAt} >= last_message_at THEN {messageId} ELSE last_message_id END,
                last_message_direction = CASE WHEN last_message_at IS NULL OR {message.OccurredAt} >= last_message_at THEN 1 ELSE last_message_direction END,
                last_message_kind      = CASE WHEN last_message_at IS NULL OR {message.OccurredAt} >= last_message_at THEN {kind} ELSE last_message_kind END,
                last_message_preview   = CASE WHEN last_message_at IS NULL OR {message.OccurredAt} >= last_message_at THEN {preview} ELSE last_message_preview END,
                last_message_status    = CASE WHEN last_message_at IS NULL OR {message.OccurredAt} >= last_message_at THEN 2 ELSE last_message_status END,
                last_message_at        = GREATEST(last_message_at, {message.OccurredAt}),
                updated_at         = {now},
                version            = version + 1
            FROM old
            WHERE id = {conversationId}
            RETURNING old.old_status AS "OldStatus", old.old_customer_id AS "OldCustomerId"
            """).ToListAsync(cancellationToken)).Single();

        if (message.Media is { } media)
        {
            // §8.6: un id que no cabe en la columna haría fallar el INSERT y con él el mensaje entero. El
            // mensaje entra; el medio queda rendido de una vez, con el motivo, y el worker nunca lo reclama.
            var valid = Media.MediaTransfer.IsValidMetaMediaId(media.MetaMediaId);
            var metaMediaId = valid ? media.MetaMediaId : string.Empty;
            var nextAttemptAt = valid ? now : Media.MediaCopyProcessor.GiveUpAt;
            var lastError = valid ? null : "invalid_media_id";
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO messaging.message_media (message_id, mime_type, file_name, meta_media_id, sha256, attempts, next_attempt_at, last_error)
                VALUES ({messageId}, {media.MimeType}, {media.FileName}, {metaMediaId}, {media.Sha256}, 0, {nextAttemptAt}, {lastError})
                """, cancellationToken);
        }

        // Spec 2026-10-10 §8.7: justo antes del mensaje que los causó, cada uno con su propio milisegundo para que el
        // orden del hilo (IX_messages_thread) no dependa de dos UUID v7 del mismo milisegundo. Orden fijo: la
        // conversación se reabre (-3 ms), queda atada a su cliente (-2 ms) y hereda el asignado de ese cliente (-1 ms).
        // Sólo se llega acá si la sentencia 2 insertó: un reenvío no repite eventos.
        var reopenedAt = message.OccurredAt.AddMilliseconds(-3);
        var customerEventAt = message.OccurredAt.AddMilliseconds(-2);
        var inheritedAt = message.OccurredAt.AddMilliseconds(-1);
        if (before.OldStatus == nameof(ConversationStatus.Resolved))
        {
            await ConversationEventRows.InsertAsync(dbContext, tenantId, connectionId, conversationId,
                new ConversationEvent(ConversationEventType.Reopened), reopenedAt, now, cancellationToken);
        }

        // §8.2: el evento lo decide la transición de customer_id de NULL a un valor. En una conversación recién creada el
        // INSERT ya lo puso, así que «antes» es la creación misma.
        var linkedNow = context.CustomerId is not null && (conversation.Created || before.OldCustomerId is null);
        if (linkedNow)
        {
            await ConversationEventRows.InsertAsync(dbContext, tenantId, connectionId, conversationId,
                new ConversationEvent(context.CustomerCreated ? ConversationEventType.CustomerCreated : ConversationEventType.CustomerLinked, CustomerId: context.CustomerId),
                customerEventAt, now, cancellationToken);
        }

        if (conversation.Created && context.InheritedMemberId is { } heir)
        {
            await ConversationEventRows.InsertAsync(dbContext, tenantId, connectionId, conversationId,
                new ConversationEvent(ConversationEventType.Inherited, Target: heir), inheritedAt, now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return messageId;
    }

    /// <summary>Spec 2026-10-10 §8.1, sentencia 1: por BSUID; si no, adopción de la fila vieja por teléfono; si no, crear
    /// con el cliente y el asignado heredado ya resueltos. <c>Created</c> sólo si esta llamada la insertó.</summary>
    private static async Task<(Guid Id, bool Created)> ResolveConversationAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, InboundMessage message, InboundContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var known = await dbContext.Database.SqlQuery<Guid>(
            $"""SELECT id AS "Value" FROM messaging.conversations WHERE connection_id = {connectionId} AND user_id = {message.UserId}""")
            .ToListAsync(cancellationToken);
        if (known.Count == 1)
        {
            return (known[0], false);
        }

        if (message.WaId is { } waId)
        {
            // 1a. Una conversación vieja (sin BSUID) con ese teléfono pasa a ser la de este BSUID (IX_conversations_connection_wa_legacy).
            var adopted = await dbContext.Database.SqlQuery<Guid>(
                $"""
                UPDATE messaging.conversations SET user_id = {message.UserId}
                WHERE connection_id = {connectionId} AND user_id IS NULL AND wa_id = {waId}
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken);
            if (adopted.Count == 1)
            {
                return (adopted[0], false);
            }
        }

        // 1b. Crear por la clave nueva. DO NOTHING + SELECT aparte, como antes: un DO UPDATE reabriría una resuelta.
        // El SELECT va en una sentencia aparte: así ve la fila que otro pod acaba de commitear.
        var newConversationId = Guid.CreateVersion7();
        var inserted = await dbContext.Database.SqlQuery<Guid>(
            $"""
            INSERT INTO messaging.conversations (id, tenant_id, connection_id, user_id, wa_id, username, parent_user_id, profile_name,
                                                 customer_id, assigned_member_id, assigned_at,
                                                 status, unread_count, last_activity_at, created_at, updated_at, version)
            VALUES ({newConversationId}, {tenantId}, {connectionId}, {message.UserId}, {message.WaId}, {message.Username}, {message.ParentUserId},
                    {message.ProfileName}, {context.CustomerId}, {context.InheritedMemberId},
                    CASE WHEN {context.InheritedMemberId}::uuid IS NULL THEN NULL ELSE {now} END,
                    'Open', 0, {message.OccurredAt}, {now}, {now}, 1)
            ON CONFLICT (connection_id, user_id) WHERE user_id IS NOT NULL DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        return inserted.Count == 1
            ? (inserted[0], true)
            : (await dbContext.Database.SqlQuery<Guid>(
                $"""SELECT id AS "Value" FROM messaging.conversations WHERE connection_id = {connectionId} AND user_id = {message.UserId}""")
                .SingleAsync(cancellationToken), false);
    }

    /// <summary>§8.7: el texto, o la leyenda de un medio; <c>null</c> si no hay. Recortado al ancho de la columna.</summary>
    public static string? Preview(InboundMessage message)
    {
        var source = message.Text ?? message.Caption;
        return source is null ? null : source.Length <= PreviewMaxLength ? source : source[..PreviewMaxLength];
    }

    private sealed record PreviousState(string OldStatus, Guid? OldCustomerId);
}
