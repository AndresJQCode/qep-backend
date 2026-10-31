using System.Data.Common;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Npgsql;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>§8.3: la transacción mantiene un candado sobre <b>una</b> fila de mensaje mientras Meta responde
/// (≤ 10 s); bloquea sólo a otro request con el mismo clientId, nunca la conversación. Soltarla antes de llamar
/// a Meta dejaría ver la fila a medio enviar y permitiría que un segundo request con el mismo clientId
/// la devolviera como enviada (o la reenviara) antes de saber qué contestó Meta.</summary>
internal sealed class OutboundMessages(MessagingDbContext dbContext) : IOutboundMessages
{
    public async Task<IOutboundClaim> ClaimAsync(OutboundDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Un segundo request con el mismo clientId espera acá hasta que el primero cierre su transacción.
            var kind = MessageColumnCodes.ToCode(draft.Kind);
            var authorKind = AuthorKindOf(draft);
            var inserted = await dbContext.Database.SqlQuery<Guid>(
                $"""
                INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, caption, client_id, sent_by_member_id, created_at,
                                                reply_to_message_id, reply_to_wamid, details, sent_by_kind, sent_by_chatbot_id, sent_by_api_key_id, sent_by_api_key_name)
                VALUES ({draft.MessageId}, {draft.ConversationId}, {draft.TenantId}, {draft.ConnectionId}, {draft.Now}, 2, {kind}, 1, {draft.Text}, {draft.Caption}, {draft.ClientId}, {draft.SentByMemberId}, {draft.Now},
                        {draft.ReplyToMessageId}, {draft.ReplyToWamid}, {draft.DetailsJson}::jsonb, {authorKind}, {draft.SentByChatbotId}, {draft.SentByApiKey?.Id}, {draft.SentByApiKey?.Name})
                ON CONFLICT (conversation_id, client_id) WHERE client_id IS NOT NULL DO NOTHING
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken);
            if (inserted.Count == 1)
            {
                return new Claim(dbContext, transaction, draft, existing: null);
            }

            // En una sentencia aparte: así ve la fila que el otro request acaba de commitear.
            var rows = await dbContext.Database.SqlQuery<ExistingRow>(
                $"""
                SELECT m.id AS "Id", m.status AS "Status", m.occurred_at AS "OccurredAt", m.sent_by_member_id AS "SentByMemberId", m.sent_by_chatbot_id AS "SentByChatbotId", coalesce(m.text, '') AS "Text",
                       m.reply_to_message_id AS "ReplyToMessageId", m.kind AS "Kind", mm.mime_type AS "MediaMimeType", m.details::text AS "DetailsJson",
                       mm.file_name AS "MediaFileName", m.caption AS "Caption", m.failure_code AS "FailureCode",
                       m.sent_by_api_key_id AS "SentByApiKeyId", m.sent_by_api_key_name AS "SentByApiKeyName"
                FROM messaging.messages m LEFT JOIN messaging.message_media mm ON mm.message_id = m.id
                WHERE m.conversation_id = {draft.ConversationId} AND m.client_id = {draft.ClientId} FOR UPDATE OF m
                """).ToListAsync(cancellationToken);
            return new Claim(dbContext, transaction, draft, ToExisting(rows.Single()));
        }
        catch (PostgresException exception) when (exception is { SqlState: "23505", ConstraintName: MessagingDbContext.ApiClientIndex })
        {
            // Spec 2026-10-30 §4.4: el mismo clientId de una clave ya salió en otra conversación del tenant. La del mismo
            // (conversación, clientId) no llega aquí: la absorbe el ON CONFLICT de arriba.
            await transaction.DisposeAsync();
            throw new ResourceConflictException(
                MessagingErrorCodes.ExternalClientIdReused, "The clientId was already used by this tenant in another conversation.", exception);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    public async Task<ExistingApiMessage?> FindApiMessageAsync(Guid tenantId, Guid clientId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database.SqlQuery<ApiMessageRow>(
            $"""
            SELECT m.id AS "Id", m.conversation_id AS "ConversationId", m.connection_id AS "ConnectionId", c.wa_id AS "WaId", m.status AS "Status",
                   m.failure_code AS "FailureCode", m.details::text AS "DetailsJson"
            FROM messaging.messages m JOIN messaging.conversations c ON c.id = m.conversation_id
            WHERE m.tenant_id = {tenantId} AND m.client_id = {clientId} AND m.sent_by_kind = 4
            """).ToListAsync(cancellationToken);
        return rows.Count == 1
            ? new ExistingApiMessage(rows[0].Id, rows[0].ConversationId, rows[0].ConnectionId, rows[0].WaId, MessageColumnCodes.ToStatus(rows[0].Status), rows[0].FailureCode, rows[0].DetailsJson)
            : null;
    }

    public async Task<ExistingOutbound?> FindByClientIdAsync(Guid conversationId, Guid clientId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database.SqlQuery<ExistingRow>(
            $"""
            SELECT m.id AS "Id", m.status AS "Status", m.occurred_at AS "OccurredAt", m.sent_by_member_id AS "SentByMemberId", m.sent_by_chatbot_id AS "SentByChatbotId",
                   coalesce(m.text, '') AS "Text", m.reply_to_message_id AS "ReplyToMessageId", m.kind AS "Kind", mm.mime_type AS "MediaMimeType", m.details::text AS "DetailsJson",
                   mm.file_name AS "MediaFileName", m.caption AS "Caption", m.failure_code AS "FailureCode",
                   m.sent_by_api_key_id AS "SentByApiKeyId", m.sent_by_api_key_name AS "SentByApiKeyName"
            FROM messaging.messages m LEFT JOIN messaging.message_media mm ON mm.message_id = m.id
            WHERE m.conversation_id = {conversationId} AND m.client_id = {clientId}
            """).ToListAsync(cancellationToken);
        return rows.Count == 1 ? ToExisting(rows[0]) : null;
    }

    /// <summary>Los CHECK de la tabla (spec 2026-10-21 §5.5 y 2026-10-30 §4.4): <c>sent_by_kind = 2</c> si y sólo si hay chatbot y <c>4</c> si y
    /// sólo si hay clave de API, y en ambos casos sin miembro.</summary>
    private static short AuthorKindOf(OutboundDraft draft)
    {
        // Exactamente un autor: con dos, o con ninguno, los CHECK saltarían después (o se escribiría una fila kind 1 sin miembro);
        // mejor fallar aquí, antes de abrir nada contra la tabla.
        var authors = (draft.SentByMemberId is null ? 0 : 1) + (draft.SentByChatbotId is null ? 0 : 1) + (draft.SentByApiKey is null ? 0 : 1);
        if (authors != 1)
        {
            throw new ArgumentException("A draft needs exactly one author: SentByMemberId, SentByChatbotId or SentByApiKey.", nameof(draft));
        }

        return (short)(draft.SentByChatbotId is not null ? MessageAuthorKind.Chatbot : draft.SentByApiKey is not null ? MessageAuthorKind.ApiKey : MessageAuthorKind.Member);
    }

    private static ExistingOutbound ToExisting(ExistingRow row) =>
        new(row.Id, MessageColumnCodes.ToStatus(row.Status), row.OccurredAt, row.SentByMemberId, row.Text, row.ReplyToMessageId,
            MessageColumnCodes.ToKind(row.Kind), row.MediaMimeType, row.DetailsJson, row.MediaFileName, row.Caption, row.SentByChatbotId,
            row.FailureCode, row.SentByApiKeyId, row.SentByApiKeyName);

    private sealed class ExistingRow
    {
        public Guid Id { get; set; }

        public short Status { get; set; }

        public DateTimeOffset OccurredAt { get; set; }

        public Guid? SentByMemberId { get; set; }

        public Guid? SentByChatbotId { get; set; }

        public string Text { get; set; } = string.Empty;

        public Guid? ReplyToMessageId { get; set; }

        public short Kind { get; set; }

        public string? MediaMimeType { get; set; }

        public string? DetailsJson { get; set; }

        public string? MediaFileName { get; set; }

        public string? Caption { get; set; }

        public int? FailureCode { get; set; }

        public Guid? SentByApiKeyId { get; set; }

        public string? SentByApiKeyName { get; set; }
    }

    private sealed class ApiMessageRow
    {
        public Guid Id { get; set; }

        public Guid ConversationId { get; set; }

        public Guid ConnectionId { get; set; }

        public string? WaId { get; set; }

        public short Status { get; set; }

        public int? FailureCode { get; set; }

        public string? DetailsJson { get; set; }
    }

    private sealed class Claim(MessagingDbContext dbContext, IDbContextTransaction transaction, OutboundDraft draft, ExistingOutbound? existing) : IOutboundClaim
    {
        private bool _closed;

        public bool Inserted => existing is null;

        public Guid MessageId => existing?.Id ?? draft.MessageId;

        public Guid ConversationId => draft.ConversationId;

        public ExistingOutbound? Existing => existing;

        public async Task CommitSentAsync(string wamid, DateTimeOffset occurredAt, string? metaMediaId, CancellationToken cancellationToken)
        {
            // Una imagen o un documento no tienen texto: la foto de la conversación muestra su pie (como la ingesta, Text ?? Caption).
            var preview = PreviewOf(draft.Text ?? draft.Caption);
            var kind = MessageColumnCodes.ToCode(draft.Kind);
            // Reenviar un Failed puede cambiar de autor: kind, miembro, chatbot y clave se reescriben juntos para no violar los CHECK.
            var authorKind = AuthorKindOf(draft);
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.messages SET status = 1, wamid = {wamid}, occurred_at = {occurredAt}, text = {draft.Text}, caption = {draft.Caption}, kind = {kind},
                    failure_code = NULL, failure_title = NULL, sent_by_member_id = {draft.SentByMemberId},
                    sent_by_kind = {authorKind}, sent_by_chatbot_id = {draft.SentByChatbotId},
                    sent_by_api_key_id = {draft.SentByApiKey?.Id}, sent_by_api_key_name = {draft.SentByApiKey?.Name},
                    reply_to_message_id = {draft.ReplyToMessageId}, reply_to_wamid = {draft.ReplyToWamid}, details = {draft.DetailsJson}::jsonb
                WHERE id = {MessageId}
                """, cancellationToken);
            await UpsertMediaAsync(occurredAt, metaMediaId, cancellationToken);
            // La foto de la conversación, como la ingesta de §7.5: CASE por fecha para no pisar un entrante más
            // nuevo. No sube version ni updated_at: el envío no es una edición que la persona pueda pisar, y subirla
            // convertiría un resolver justo después de responder en un 412 (mismo criterio que los acuses de §7.5).
            // Una nota de voz deja kind 4 y preview NULL, igual que un audio entrante (spec 2026-10-11 §6.5).
            // Spec 2026-10-29 (regla R9): responder desde QEP saca la conversación de la regla de la importación; una parte del
            // historial que llegue después no la cierra a espaldas del asesor. Tampoco sube version, por lo mismo de arriba.
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.conversations SET
                    history_managed        = false,
                    last_activity_at       = GREATEST(last_activity_at, {occurredAt}),
                    last_message_id        = CASE WHEN last_message_at IS NULL OR {occurredAt} >= last_message_at THEN {MessageId} ELSE last_message_id END,
                    last_message_direction = CASE WHEN last_message_at IS NULL OR {occurredAt} >= last_message_at THEN 2 ELSE last_message_direction END,
                    last_message_kind      = CASE WHEN last_message_at IS NULL OR {occurredAt} >= last_message_at THEN {kind} ELSE last_message_kind END,
                    last_message_preview   = CASE WHEN last_message_at IS NULL OR {occurredAt} >= last_message_at THEN {preview} ELSE last_message_preview END,
                    last_message_status    = CASE WHEN last_message_at IS NULL OR {occurredAt} >= last_message_at THEN 1 ELSE last_message_status END,
                    last_message_at        = GREATEST(last_message_at, {occurredAt})
                WHERE id = {draft.ConversationId}
                """, cancellationToken);
            // Cerrado antes del commit: si el commit falla, DisposeAsync no intenta un rollback que taparía esa falla.
            _closed = true;
            await transaction.CommitAsync(cancellationToken);
        }

        public async Task CommitFailedAsync(int failureCode, string? failureTitle, DateTimeOffset occurredAt, string? metaMediaId, CancellationToken cancellationToken)
        {
            var kind = MessageColumnCodes.ToCode(draft.Kind);
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.messages SET status = 4, failure_code = {failureCode}, failure_title = {failureTitle}, occurred_at = {occurredAt}, text = {draft.Text}, caption = {draft.Caption}, kind = {kind},
                    reply_to_message_id = {draft.ReplyToMessageId}, reply_to_wamid = {draft.ReplyToWamid}, details = {draft.DetailsJson}::jsonb
                WHERE id = {MessageId}
                """, cancellationToken);
            // D-V1: la burbuja fallida se escucha desde otro dispositivo, y el reintento sobrescribe en vez de duplicar.
            await UpsertMediaAsync(occurredAt, metaMediaId, cancellationToken);
            // Cerrado antes del commit: si el commit falla, DisposeAsync no intenta un rollback que taparía esa falla.
            _closed = true;
            await transaction.CommitAsync(cancellationToken);
        }

        private static string? PreviewOf(string? text) =>
            text is null ? null : text.Length <= Conversation.PreviewMaxLength ? text : text[..Conversation.PreviewMaxLength];

        /// <summary>Spec 2026-10-11 §6.5 y §7: con <c>stored_at</c> no nulo la fila queda fuera de
        /// <c>IX_message_media_pending</c> y el <c>MediaCopyWorker</c> no la toma. <c>meta_media_id</c> es
        /// <c>NOT NULL</c>: sin id de Meta se guarda <c>''</c> (D-V2). La clave es la de <see cref="MessageId"/>, que en
        /// un reintento es la de la fila <c>Failed</c>: R2 sobrescribe el mismo objeto.</summary>
        private async Task UpsertMediaAsync(DateTimeOffset occurredAt, string? metaMediaId, CancellationToken cancellationToken)
        {
            if (draft.Media is not { } media)
            {
                return;
            }

            var key = MessagingMediaKeys.For(draft.TenantId, MessageId);
            var mediaId = metaMediaId ?? string.Empty;
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO messaging.message_media (message_id, mime_type, file_name, meta_media_id, sha256, size_bytes, storage_key, stored_at, attempts, next_attempt_at, last_error)
                VALUES ({MessageId}, {media.MimeType}, {media.FileName}, {mediaId}, {media.Sha256Hex}, {media.SizeBytes}, {key}, {occurredAt}, 0, {occurredAt}, NULL)
                ON CONFLICT (message_id) DO UPDATE SET
                    mime_type = EXCLUDED.mime_type, file_name = EXCLUDED.file_name, meta_media_id = EXCLUDED.meta_media_id, sha256 = EXCLUDED.sha256, size_bytes = EXCLUDED.size_bytes,
                    storage_key = EXCLUDED.storage_key, stored_at = EXCLUDED.stored_at, attempts = 0, next_attempt_at = EXCLUDED.next_attempt_at, last_error = NULL
                """, cancellationToken);
        }

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            if (!_closed)
            {
                _closed = true;
                await transaction.RollbackAsync(cancellationToken);
            }
        }

        /// <summary>Desecharlo sin cerrar es rollback. Si ese rollback falla (conexión caída tras un error previo),
        /// no se propaga: taparía la excepción original, y PostgreSQL descarta la transacción igual al cerrarse.</summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await RollbackAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is DbException or InvalidOperationException)
            {
                // Ver summary.
            }

            await transaction.DisposeAsync();
        }
    }
}
