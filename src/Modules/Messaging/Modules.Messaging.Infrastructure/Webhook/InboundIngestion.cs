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
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, InboundMessage message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await IngestOnceAsync(dbContext, tenantId, connectionId, message, now, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation && exception.ConstraintName == ConnectionUserIndex)
        {
            return await IngestOnceAsync(dbContext, tenantId, connectionId, message, now, cancellationToken);
        }
    }

    private static async Task<Guid?> IngestOnceAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, InboundMessage message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // 1. La conversación (spec 2026-10-10 §8.1).
        var conversationId = await ResolveConversationAsync(dbContext, tenantId, connectionId, message, now, cancellationToken);

        // 2. El mensaje; un reenvío no inserta nada. reply_to_wamid guarda la cita tal cual; resolverla es de la T9.
        var messageId = Guid.CreateVersion7();
        var kind = MessageColumnCodes.ToCode(message.Kind);
        var insertedMessage = await dbContext.Database.SqlQuery<Guid>(
            $"""
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, caption, details, wamid, reply_to_wamid, created_at)
            VALUES ({messageId}, {conversationId}, {tenantId}, {connectionId}, {message.OccurredAt}, 1, {kind}, 2, {message.Text}, {message.Caption}, {message.DetailsJson}::jsonb, {message.Wamid}, {message.QuotedWamid}, {now})
            ON CONFLICT (connection_id, wamid) WHERE wamid IS NOT NULL DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        if (insertedMessage.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        // 3. Contadores, reapertura, ventana y foto. Las expresiones del SET leen los valores viejos.
        var preview = Preview(message);
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.conversations SET
                unread_count       = unread_count + 1,
                status             = 'Open',
                profile_name       = COALESCE({message.ProfileName}, profile_name),
                wa_id              = COALESCE({message.WaId}, wa_id),
                username           = COALESCE({message.Username}, username),
                parent_user_id     = COALESCE({message.ParentUserId}, parent_user_id),
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
            WHERE id = {conversationId}
            """, cancellationToken);

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

        await transaction.CommitAsync(cancellationToken);
        return messageId;
    }

    /// <summary>Spec 2026-10-10 §8.1, sentencia 1: por BSUID; si no, adopción de la fila vieja por teléfono; si no, crear.</summary>
    private static async Task<Guid> ResolveConversationAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, InboundMessage message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var known = await dbContext.Database.SqlQuery<Guid>(
            $"""SELECT id AS "Value" FROM messaging.conversations WHERE connection_id = {connectionId} AND user_id = {message.UserId}""")
            .ToListAsync(cancellationToken);
        if (known.Count == 1)
        {
            return known[0];
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
                return adopted[0];
            }
        }

        // 1b. Crear por la clave nueva. DO NOTHING + SELECT aparte, como antes: un DO UPDATE reabriría una resuelta.
        // El SELECT va en una sentencia aparte: así ve la fila que otro pod acaba de commitear.
        var newConversationId = Guid.CreateVersion7();
        var inserted = await dbContext.Database.SqlQuery<Guid>(
            $"""
            INSERT INTO messaging.conversations (id, tenant_id, connection_id, user_id, wa_id, username, parent_user_id, profile_name,
                                                 status, unread_count, last_activity_at, created_at, updated_at, version)
            VALUES ({newConversationId}, {tenantId}, {connectionId}, {message.UserId}, {message.WaId}, {message.Username}, {message.ParentUserId},
                    {message.ProfileName}, 'Open', 0, {message.OccurredAt}, {now}, {now}, 1)
            ON CONFLICT (connection_id, user_id) WHERE user_id IS NOT NULL DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        return inserted.Count == 1
            ? inserted[0]
            : await dbContext.Database.SqlQuery<Guid>(
                $"""SELECT id AS "Value" FROM messaging.conversations WHERE connection_id = {connectionId} AND user_id = {message.UserId}""")
                .SingleAsync(cancellationToken);
    }

    /// <summary>§8.7: el texto, o la leyenda de un medio; <c>null</c> si no hay. Recortado al ancho de la columna.</summary>
    public static string? Preview(InboundMessage message)
    {
        var source = message.Text ?? message.Caption;
        return source is null ? null : source.Length <= PreviewMaxLength ? source : source[..PreviewMaxLength];
    }
}
