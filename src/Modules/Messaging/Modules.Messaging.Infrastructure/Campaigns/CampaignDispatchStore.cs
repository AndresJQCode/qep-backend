using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Campaigns;

/// <summary>
/// Spec 2026-10-11 §6.3: la persistencia del worker, en SQL. Dos reglas la gobiernan. (1) Ningún método retiene una transacción mientras se
/// llama a Meta: eso es lo que separa a una campaña de miles del envío humano, que sí lo hace con una fila bloqueada. (2) El mensaje
/// saliente nace <c>Failed</c> sin confirmar (<c>failure_code = -1</c>) y sólo pasa a <c>Sent</c> cuando Meta lo acepta: si el proceso cae con la
/// llamada en vuelo, la burbuja ya dice la verdad y la recuperación deja al destinatario en <c>unknown_outcome</c> sin reenviar. Las
/// transiciones de la campaña llevan su auditoría en la misma transacción (<see cref="IMessagingAuditRecorder"/> acumula en el
/// <c>DbContext</c> y el <c>SaveChanges</c> la escribe junto con el resto).
/// </summary>
internal sealed class CampaignDispatchStore(MessagingDbContext dbContext, IConversationOpener opener, IMessagingAuditRecorder audit) : ICampaignDispatchStore
{
    /// <summary>Spec 2026-10-11 §6.3 paso 3.2: smallint de <c>MessageKind.Template</c>, <c>MessageDirection.Outbound</c> y los estados.</summary>
    private const short TemplateKind = 11;
    private const short Outbound = 2;
    private const short StatusSent = 1;
    private const short StatusFailed = 4;

    public async Task<int> ActivateDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Una sola sentencia con el estado como guarda: dos pods no activan dos veces la misma, y SKIP LOCKED evita esperarse.
        var activated = await dbContext.Database.SqlQuery<Guid>(
            $"""
            UPDATE messaging.campaigns SET status = 'Sending', started_at = {now}, updated_at = {now}, version = version + 1
             WHERE id IN (SELECT id FROM messaging.campaigns WHERE status = 'Scheduled' AND scheduled_at <= {now} FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        return activated.Count;
    }

    public async Task<IReadOnlyList<CampaignFreezeTarget>> ListUnfrozenAsync(CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database.SqlQuery<FreezeRow>(
            $"""
            SELECT id AS "Id", tenant_id AS "TenantId", template_category AS "TemplateCategory", audience_tag_ids AS "TagIds", audience_customer_ids AS "CustomerIds"
              FROM messaging.campaigns
             WHERE status = 'Sending' AND audience_frozen_at IS NULL
             ORDER BY started_at
            """).ToListAsync(cancellationToken);
        return [.. rows.Select(row => new CampaignFreezeTarget(row.Id, row.TenantId, row.TemplateCategory, row.TagIds, row.CustomerIds))];
    }

    public async Task AddRecipientsAsync(CampaignFreezeTarget campaign, IReadOnlyList<CampaignRecipientSeed> batch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(batch);
        var ids = batch.Select(_ => Guid.CreateVersion7()).ToArray();
        var customerIds = batch.Select(seed => seed.CustomerId).ToArray();
        var names = batch.Select(seed => seed.CustomerName).ToArray();
        var waIds = batch.Select(seed => seed.WaId).ToArray();
        var userIds = batch.Select(seed => seed.UserId).ToArray();
        var statuses = batch.Select(seed => seed.Status.ToString()).ToArray();
        var codes = batch.Select(seed => seed.FailureCode).ToArray();
        var reasons = batch.Select(seed => CampaignFailureReasons.For(seed.FailureCode)).ToArray();
        // ON CONFLICT DO NOTHING: repetir el congelado (porque un pod cayó a la mitad, o dos lo hicieron a la vez) es inocuo.
        // R9: y sólo mientras la campaña sigue Sending; si alguien la canceló a mitad de un congelado largo, los lotes siguientes no insertan nada.
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO messaging.campaign_recipients (id, campaign_id, tenant_id, customer_id, customer_name, wa_id, user_id, status, failure_code, failure_reason, attempts, next_attempt_at)
            SELECT s.id, {campaign.CampaignId}, {campaign.TenantId}, s.customer_id, s.customer_name, s.wa_id, s.user_id, s.status, s.failure_code, s.failure_reason, 0, {now}
              FROM unnest({ids}, {customerIds}, {names}, {waIds}, {userIds}, {statuses}, {codes}, {reasons})
                   AS s(id, customer_id, customer_name, wa_id, user_id, status, failure_code, failure_reason)
             WHERE EXISTS (SELECT 1 FROM messaging.campaigns c WHERE c.id = {campaign.CampaignId} AND c.status = 'Sending')
            ON CONFLICT (campaign_id, customer_id) DO NOTHING
            """, cancellationToken);
    }

    public async Task MarkFrozenAsync(Guid campaignId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Contabilidad interna: sin subir la versión, así el «Cancelar» de quien está mirando la pantalla no choca con un 412.
        await dbContext.Database.ExecuteSqlAsync(
            $"UPDATE messaging.campaigns SET audience_frozen_at = {now} WHERE id = {campaignId} AND audience_frozen_at IS NULL", cancellationToken);

        // R9: si la campaña terminó mientras se escribía la audiencia, un lote en vuelo pudo dejar Pending después de que Cancelar (o la
        // detención) barrió los suyos. Esos rezagados nunca se enviarían, pero tampoco dejarían de contar como pendientes.
        var cancelled = CampaignFailureReasons.For(CampaignFailureCodes.CampaignCancelled);
        var failed = CampaignFailureReasons.For(CampaignFailureCodes.CampaignFailed);
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.campaign_recipients r SET
                status         = 'Skipped',
                failure_code   = CASE WHEN c.status = 'Cancelled' THEN {CampaignFailureCodes.CampaignCancelled} ELSE {CampaignFailureCodes.CampaignFailed} END,
                failure_reason = CASE WHEN c.status = 'Cancelled' THEN {cancelled} ELSE {failed} END,
                locked_until   = NULL
              FROM messaging.campaigns c
             WHERE c.id = r.campaign_id AND r.campaign_id = {campaignId} AND r.status = 'Pending' AND c.status IN ('Cancelled', 'Failed')
            """, cancellationToken);
    }

    public async Task<IReadOnlyList<ClaimedRecipient>> ClaimAsync(int take, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        // A9: nada se reclama antes de que la audiencia esté congelada. El congelado de otro pod escribe lotes de 500 que ya son Pending y el
        // desduplicado por número (CD-12) corre al terminar, dentro de MarkFrozenAsync: reclamar antes dejaría salir a un destinatario
        // que ese UPDATE todavía puede descartar. Compatible hacia atrás: una campaña congelada antes del despliegue ya tiene la marca, y
        // una que quedó a medias (el pod murió) la recupera ListUnfrozenAsync, que repite el congelado (inocuo) y termina en MarkFrozenAsync.
        // R5: reparto por turnos. Primero se numera a los vencidos dentro de cada campaña (una campaña grande no se come el lote de las
        // chicas) y luego dentro de cada conexión por ese número (el ritmo es por conexión: una con mucha cola no frena a las demás); se
        // toman los de número más bajo. `picked` no bloquea —una ventana no se lleva con FOR UPDATE—; el candado va en `due`, que vuelve a
        // exigir Pending por si otro pod ganó la carrera entre los dos pasos (con SKIP LOCKED, perder da menos filas, nunca repetidas).
        var rows = await dbContext.Database.SqlQuery<ClaimRow>(
            $"""
            WITH ranked AS (
                SELECT r.id, c.connection_id, r.next_attempt_at,
                       ROW_NUMBER() OVER (PARTITION BY r.campaign_id ORDER BY r.next_attempt_at, r.id) AS campaign_rank
                  FROM messaging.campaign_recipients r
                  JOIN messaging.campaigns c ON c.id = r.campaign_id
                 WHERE r.status = 'Pending' AND r.next_attempt_at <= {now} AND c.status = 'Sending' AND c.audience_frozen_at IS NOT NULL),
            fair AS (
                SELECT id, next_attempt_at,
                       ROW_NUMBER() OVER (PARTITION BY connection_id ORDER BY campaign_rank, next_attempt_at, id) AS connection_rank
                  FROM ranked
                 WHERE campaign_rank <= {take}),
            picked AS (
                SELECT id FROM fair ORDER BY connection_rank, next_attempt_at, id LIMIT {take}),
            due AS (
                SELECT r.id FROM messaging.campaign_recipients r
                 WHERE r.id IN (SELECT id FROM picked) AND r.status = 'Pending'
                 FOR UPDATE OF r SKIP LOCKED)
            UPDATE messaging.campaign_recipients r
               SET status = 'Sending', locked_until = {now} + {lease}, attempts = r.attempts + 1
              FROM due, messaging.campaigns c
             WHERE r.id = due.id AND c.id = r.campaign_id
            RETURNING r.id AS "Id", r.campaign_id AS "CampaignId", r.tenant_id AS "TenantId", r.customer_id AS "CustomerId", r.customer_name AS "CustomerName",
                      r.wa_id AS "WaId", r.user_id AS "UserId", r.attempts AS "Attempts", r.message_id AS "MessageId", r.conversation_id AS "ConversationId",
                      c.connection_id AS "ConnectionId", c.template_name AS "TemplateName", c.template_language AS "TemplateLanguage",
                      c.template_category AS "TemplateCategory", c.parameters::text AS "Parameters", c.created_by_member_id AS "CreatedByMemberId"
            """).ToListAsync(cancellationToken);

        // R2: los parámetros se leen fila por fila. Un JSON ilegible falla a esa campaña, no a todo el lote reclamado (que quedaría en
        // Sending hasta vencer el lease, y el próximo tick volvería a reventar igual).
        var parsed = new Dictionary<Guid, IReadOnlyDictionary<string, CampaignParameter>?>();
        var claimed = new List<ClaimedRecipient>(rows.Count);
        foreach (var row in rows)
        {
            if (!parsed.TryGetValue(row.CampaignId, out var parameters))
            {
                parameters = TryParseParameters(row.Parameters);
                parsed[row.CampaignId] = parameters;
            }

            if (parameters is null)
            {
                await FailUnreadableAsync(row, now, cancellationToken);
                continue;
            }

            claimed.Add(new ClaimedRecipient(
                row.Id, row.CampaignId, row.TenantId, row.CustomerId, row.CustomerName, row.WaId, row.UserId, row.Attempts, row.MessageId, row.ConversationId,
                row.ConnectionId, row.TemplateName, row.TemplateLanguage, row.TemplateCategory, parameters, row.CreatedByMemberId));
        }

        return claimed;
    }

    private static IReadOnlyDictionary<string, CampaignParameter>? TryParseParameters(string? json)
    {
        try
        {
            return CampaignParametersJson.Parse(json);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>El destinatario <c>Failed invalid_parameters</c> y la campaña detenida con el mismo motivo (sus <c>Pending</c> pasan a
    /// <c>Skipped campaign_failed</c>).</summary>
    private async Task FailUnreadableAsync(ClaimRow row, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reason = CampaignFailureReasons.For(CampaignFailureCodes.InvalidParameters);
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.campaign_recipients
               SET status = 'Failed', failure_code = {CampaignFailureCodes.InvalidParameters}, failure_reason = {reason}, locked_until = NULL
             WHERE id = {row.Id} AND status = 'Sending' AND attempts = {row.Attempts}
            """, cancellationToken);
        await StopCampaignAsync(row.CampaignId, row.TenantId, CampaignFailureCodes.InvalidParameters, now, cancellationToken);
    }

    public async Task<PreparedSend?> PrepareAsync(ClaimedRecipient recipient, string text, string detailsJson, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        // Una campaña cancelada o detenida mientras este destinatario esperaba su turno ya no envía: nada en vuelo todavía, así que se omite.
        var status = await dbContext.Database.SqlQuery<string>(
            $"""SELECT status AS "Value" FROM messaging.campaigns WHERE id = {recipient.CampaignId}""").SingleAsync(cancellationToken);
        if (status != nameof(CampaignStatus.Sending))
        {
            await SkipAsync(recipient, status == nameof(CampaignStatus.Failed) ? CampaignFailureCodes.CampaignFailed : CampaignFailureCodes.CampaignCancelled, cancellationToken);
            return null;
        }

        // Un reintento ya tiene su mensaje y su conversación: el mismo mensaje se envía otra vez, no se duplica. Pero también hay que seguir
        // siendo el dueño (R1): la misma guarda de abajo, sin insertar nada.
        if (recipient is { MessageId: { } existingMessage, ConversationId: { } existingConversation })
        {
            var stillOwned = await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.campaign_recipients SET message_id = {existingMessage}, conversation_id = {existingConversation}
                 WHERE id = {recipient.Id} AND status = 'Sending' AND locked_until > {now} AND attempts = {recipient.Attempts}
                """, cancellationToken);
            return stillOwned == 1 ? new PreparedSend(existingMessage, existingConversation) : null;
        }

        var contact = new MessagingCustomerContact(
            recipient.CustomerId, recipient.CustomerName, recipient.WaId is null ? null : "+" + recipient.WaId, recipient.UserId, IsActive: true, MarketingOptOut: false);
        // §6.3 paso 3.1: sin asignar. Una respuesta entra por la ingesta normal y cae en «Sin asignar», donde un asesor la toma.
        var opened = await opener.OpenAsync(recipient.TenantId, recipient.ConnectionId, contact, assignedMemberId: null, actorUserId: null, now, cancellationToken);
        var messageId = recipient.MessageId ?? Guid.CreateVersion7();

        // Paso 3.2: mensaje y message_id del destinatario, en la misma transacción. client_id = el destinatario: la clave única
        // (conversation_id, client_id) garantiza a lo sumo un mensaje por destinatario aunque dos pods lleguen a este punto.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var inserted = await dbContext.Database.SqlQuery<Guid>(
            $"""
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, client_id, sent_by_member_id, created_at,
                                            failure_code, details)
            VALUES ({messageId}, {opened.ConversationId}, {recipient.TenantId}, {recipient.ConnectionId}, {now}, {Outbound}, {TemplateKind}, {StatusFailed}, {text}, {recipient.Id},
                    {recipient.CreatedByMemberId}, {now}, {MessageFailureReasons.Unconfirmed}, {detailsJson}::jsonb)
            ON CONFLICT (conversation_id, client_id) WHERE client_id IS NOT NULL DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        if (inserted.Count == 0)
        {
            messageId = await dbContext.Database.SqlQuery<Guid>(
                $"""SELECT id AS "Value" FROM messaging.messages WHERE conversation_id = {opened.ConversationId} AND client_id = {recipient.Id}""").SingleAsync(cancellationToken);
        }

        // R1, compare-and-set: ata el mensaje sólo si este worker sigue siendo el dueño —en Sending, con el lease vivo y con el mismo número
        // de intento que reclamó—. Si el lease venció y otro lo reclamó, el intento ya no coincide: 0 filas, se deshace el mensaje y no se llama a Meta.
        var attached = await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.campaign_recipients SET message_id = {messageId}, conversation_id = {opened.ConversationId}
             WHERE id = {recipient.Id} AND status = 'Sending' AND locked_until > {now} AND attempts = {recipient.Attempts}
            """, cancellationToken);
        if (attached != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return new PreparedSend(messageId, opened.ConversationId);
    }

    public async Task SettleSentAsync(ClaimedRecipient recipient, PreparedSend prepared, string wamid, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(prepared);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.messages SET status = {StatusSent}, wamid = {wamid}, occurred_at = {now}, failure_code = NULL, failure_title = NULL
             WHERE id = {prepared.MessageId}
            """, cancellationToken);
        // La foto de la conversación, igual que el envío humano: CASE por fecha para no pisar un entrante más nuevo, sin subir la versión
        // (un envío no es una edición que la persona pueda pisar).
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.conversations c SET
                last_activity_at       = GREATEST(c.last_activity_at, {now}),
                last_message_id        = CASE WHEN c.last_message_at IS NULL OR {now} >= c.last_message_at THEN m.id ELSE c.last_message_id END,
                last_message_direction = CASE WHEN c.last_message_at IS NULL OR {now} >= c.last_message_at THEN {Outbound} ELSE c.last_message_direction END,
                last_message_kind      = CASE WHEN c.last_message_at IS NULL OR {now} >= c.last_message_at THEN {TemplateKind} ELSE c.last_message_kind END,
                last_message_preview   = CASE WHEN c.last_message_at IS NULL OR {now} >= c.last_message_at THEN left(m.text, {Conversation.PreviewMaxLength}) ELSE c.last_message_preview END,
                last_message_status    = CASE WHEN c.last_message_at IS NULL OR {now} >= c.last_message_at THEN {StatusSent} ELSE c.last_message_status END,
                last_message_at        = GREATEST(c.last_message_at, {now})
              FROM messaging.messages m
             WHERE m.id = {prepared.MessageId} AND c.id = m.conversation_id
            """, cancellationToken);
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.campaign_recipients
               SET status = 'Sent', sent_at = {now}, locked_until = NULL, failure_code = NULL, failure_reason = NULL
             WHERE id = {recipient.Id}
               AND ((status = 'Sending' AND attempts = {recipient.Attempts})
                    -- Si el lease venció con la llamada en vuelo, la recuperación lo dejó unknown_outcome; la respuesta de Meta es la verdad.
                    OR (status = 'Failed' AND failure_code = {CampaignFailureCodes.UnknownOutcome} AND message_id = {prepared.MessageId}))
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SettleFailedAsync(
        ClaimedRecipient recipient, PreparedSend? prepared, string code, int? graphCode, string? title, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        var reason = CampaignFailureReasons.For(code);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        // R1: primero el destinatario, con la guarda de propiedad; si ya no es de este worker no se toca ni su mensaje.
        var settled = await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.campaign_recipients
               SET status = 'Failed', failure_code = {code}, failure_reason = {reason}, locked_until = NULL
             WHERE id = {recipient.Id} AND status = 'Sending' AND attempts = {recipient.Attempts}
            """, cancellationToken);
        if (settled != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        if (prepared is not null)
        {
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.messages SET status = {StatusFailed}, failure_code = {graphCode ?? MessageFailureReasons.Unconfirmed}, failure_title = {title}, occurred_at = {now}
                 WHERE id = {prepared.MessageId}
                """, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ScheduleRetryAsync(ClaimedRecipient recipient, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.campaign_recipients SET status = 'Pending', next_attempt_at = {nextAttemptAt}, locked_until = NULL
             WHERE id = {recipient.Id} AND status = 'Sending' AND attempts = {recipient.Attempts}
            """, cancellationToken);
    }

    public async Task DeferAsync(ClaimedRecipient recipient, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        // R4: `attempts - 1` devuelve el intento que sumó el reclamo. La guarda usa el valor reclamado, así que un worker que ya perdió el
        // destinatario no le resta un intento a su nuevo dueño.
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.campaign_recipients SET status = 'Pending', attempts = attempts - 1, next_attempt_at = {nextAttemptAt}, locked_until = NULL
             WHERE id = {recipient.Id} AND status = 'Sending' AND attempts = {recipient.Attempts}
            """, cancellationToken);
    }

    public async Task SkipAsync(ClaimedRecipient recipient, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        var reason = CampaignFailureReasons.For(code);
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.campaign_recipients SET status = 'Skipped', failure_code = {code}, failure_reason = {reason}, locked_until = NULL
             WHERE id = {recipient.Id} AND status = 'Sending' AND attempts = {recipient.Attempts}
            """, cancellationToken);
    }

    public async Task<bool> StopCampaignAsync(Guid campaignId, Guid tenantId, string code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reason = CampaignFailureReasons.For(code);
        var skipped = CampaignFailureReasons.For(CampaignFailureCodes.CampaignFailed);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var stopped = await dbContext.Database.SqlQuery<Guid>(
            $"""
            UPDATE messaging.campaigns
               SET status = 'Failed', failure_code = {code}, failure_reason = {reason}, completed_at = {now}, updated_at = {now}, version = version + 1
             WHERE id = {campaignId} AND tenant_id = {tenantId} AND status = 'Sending'
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        if (stopped.Count == 0)
        {
            // Otro envío de la misma campaña ya la detuvo (o se canceló): nada que hacer ni que auditar.
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.campaign_recipients
               SET status = 'Skipped', failure_code = {CampaignFailureCodes.CampaignFailed}, failure_reason = {skipped}, locked_until = NULL
             WHERE campaign_id = {campaignId} AND status = 'Pending'
            """, cancellationToken);
        audit.RecordCampaign(tenantId, Guid.Empty, bySystem: true, MessagingAuditActions.CampaignFailed, campaignId, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<int> RecoverExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var unknown = CampaignFailureReasons.For(CampaignFailureCodes.UnknownOutcome);
        var notSent = CampaignFailureReasons.For(CampaignFailureCodes.NotSent);
        var maxAttempts = CampaignDispatcher.MaxAttempts;
        // Con mensaje atado, la llamada a Meta pudo haber salido: decisión 12, no se reenvía. Sin mensaje, nunca se llamó a Meta: vuelve a
        // Pending, salvo que ya haya gastado todos sus intentos (R2) —un worker que muere siempre antes de insertar el mensaje no puede
        // reciclar al destinatario para siempre—, y entonces queda Failed not_sent, que dice la verdad: el mensaje nunca salió.
        var recovered = await dbContext.Database.SqlQuery<Guid>(
            $"""
            UPDATE messaging.campaign_recipients SET
                status         = CASE WHEN message_id IS NULL AND attempts < {maxAttempts} THEN 'Pending' ELSE 'Failed' END,
                failure_code   = CASE WHEN message_id IS NOT NULL THEN {CampaignFailureCodes.UnknownOutcome}
                                      WHEN attempts >= {maxAttempts} THEN {CampaignFailureCodes.NotSent} ELSE failure_code END,
                failure_reason = CASE WHEN message_id IS NOT NULL THEN {unknown}
                                      WHEN attempts >= {maxAttempts} THEN {notSent} ELSE failure_reason END,
                next_attempt_at = CASE WHEN message_id IS NULL AND attempts < {maxAttempts} THEN {now} ELSE next_attempt_at END,
                locked_until   = NULL
             WHERE id IN (SELECT id FROM messaging.campaign_recipients WHERE status = 'Sending' AND locked_until < {now} FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        return recovered.Count;
    }

    public async Task<int> CloseFinishedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var closed = await dbContext.Database.SqlQuery<ClosedRow>(
            $"""
            UPDATE messaging.campaigns c SET status = 'Completed', completed_at = {now}, updated_at = {now}, version = c.version + 1
             WHERE c.status = 'Sending' AND c.audience_frozen_at IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM messaging.campaign_recipients r WHERE r.campaign_id = c.id AND r.status IN ('Pending', 'Sending'))
            RETURNING c.id AS "Id", c.tenant_id AS "TenantId"
            """).ToListAsync(cancellationToken);
        foreach (var campaign in closed)
        {
            audit.RecordCampaign(campaign.TenantId, Guid.Empty, bySystem: true, MessagingAuditActions.CampaignCompleted, campaign.Id, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return closed.Count;
    }

    private sealed class FreezeRow
    {
        public Guid Id { get; set; }

        public Guid TenantId { get; set; }

        public string TemplateCategory { get; set; } = string.Empty;

        public Guid[] TagIds { get; set; } = [];

        public Guid[] CustomerIds { get; set; } = [];
    }

    private sealed class ClosedRow
    {
        public Guid Id { get; set; }

        public Guid TenantId { get; set; }
    }

    private sealed class ClaimRow
    {
        public Guid Id { get; set; }

        public Guid CampaignId { get; set; }

        public Guid TenantId { get; set; }

        public Guid CustomerId { get; set; }

        public string CustomerName { get; set; } = string.Empty;

        public string? WaId { get; set; }

        public string? UserId { get; set; }

        public int Attempts { get; set; }

        public Guid? MessageId { get; set; }

        public Guid? ConversationId { get; set; }

        public Guid ConnectionId { get; set; }

        public string TemplateName { get; set; } = string.Empty;

        public string TemplateLanguage { get; set; } = string.Empty;

        public string TemplateCategory { get; set; } = string.Empty;

        public string? Parameters { get; set; }

        public Guid CreatedByMemberId { get; set; }
    }
}
