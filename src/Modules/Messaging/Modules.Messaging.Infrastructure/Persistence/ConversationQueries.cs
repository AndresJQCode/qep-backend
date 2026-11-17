using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>§7.6: la lista por <c>IX_conversations_tenant_status_activity</c>; los counts en una sola
/// sentencia por <c>tenant_id</c>; la búsqueda por los GIN de trigramas (ILIKE / LIKE) y por <c>wa_id = ANY</c>.</summary>
internal sealed class ConversationQueries(MessagingDbContext dbContext) : IConversationQueries
{
    // De instancia y no estática: la subconsulta de la delegación (spec 2026-11-17 §7, chip «Agente IA · desde el chatbot X») usa el
    // DbSet de este contexto. Sólo la resuelve una conversación delegada, por la llave primaria.
    private Expression<Func<Conversation, ConversationRow>> Projection => conversation => new ConversationRow(
        conversation.Id, conversation.TenantId, conversation.ConnectionId, conversation.UserId, conversation.WaId, conversation.Username,
        conversation.ProfileName, conversation.CustomerId, conversation.AssignedMemberId, conversation.Status,
        conversation.UnreadCount, conversation.LastInboundAt, conversation.LastMessageId, conversation.LastMessageDirection, conversation.LastMessageKind,
        conversation.LastMessagePreview, conversation.LastMessageStatus, conversation.LastMessageAt, conversation.UpdatedAt, conversation.Version, conversation.Channel, conversation.ResolutionReasonId, conversation.LastActivityAt,
        conversation.AutomationStatus, conversation.AutomationStartedAt, conversation.AutomationPausedAt, conversation.AutomationPausedByMemberId, conversation.AutomationPauseReason,
        conversation.AutomationChatbotId, conversation.AutomationStep, conversation.AutomationResumedAt,
        conversation.BlockedAt, conversation.BlockedByMemberId, conversation.Imported, conversation.LastInboundWamid,
        conversation.AutomationBotKind, conversation.AutomationAgentId,
        dbContext.BotDelegations
            .Where(delegation => delegation.Id == conversation.AutomationDelegationId)
            .Select(delegation => (Guid?)delegation.ChatbotId)
            .FirstOrDefault(),
        conversation.AutomationDelegationId);

    public async Task<IReadOnlyList<ConversationRow>> ListAsync(
        Guid tenantId, ConversationListFilter filter, ConversationCursor? after, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = dbContext.Conversations.AsNoTracking().Where(conversation => conversation.TenantId == tenantId && conversation.Status == filter.Status);
        // Spec 2026-10-14 §5.2: ausente = todos los canales.
        if (filter.Channel is { } channel)
        {
            query = query.Where(conversation => conversation.Channel == channel);
        }

        // Spec qep-frontend 2026-10-15: una conversación sin cliente no tiene etiquetas y no pasa el filtro.
        if (filter.TagCustomerIds is { } tagged)
        {
            var taggedIds = tagged.ToArray();
            query = query.Where(conversation => conversation.CustomerId != null && taggedIds.Contains(conversation.CustomerId.Value));
        }

        // Spec 2026-10-21 §4.5: ausente = todas, con y sin asistente.
        query = filter.Automation switch
        {
            AutomationFilter.Any => query.Where(conversation => conversation.AutomationStatus != ConversationAutomationStatus.None),
            AutomationFilter.Active => query.Where(conversation => conversation.AutomationStatus == ConversationAutomationStatus.Active),
            AutomationFilter.Paused => query.Where(conversation => conversation.AutomationStatus == ConversationAutomationStatus.Paused),
            AutomationFilter.None => query.Where(conversation => conversation.AutomationStatus == ConversationAutomationStatus.None),
            _ => query,
        };

        if (filter.UnreadOnly)
        {
            query = query.Where(conversation => conversation.UnreadCount > 0);
        }

        query = filter.Assigned switch
        {
            // P15: sin membresía activa, «mías» no encuentra nada.
            AssignedFilter.Mine => filter.MemberId is { } me
                ? query.Where(conversation => conversation.AssignedMemberId == me)
                : query.Where(_ => false),
            AssignedFilter.Unassigned => query.Where(conversation => conversation.AssignedMemberId == null),
            _ => query,
        };
        if (filter.Search is { } search)
        {
            var pattern = "%" + Escape(search) + "%";
            var digits = ConversationSearchTerms.NumberDigits(search);
            var digitsPattern = digits is null ? null : "%" + digits + "%";
            var phones = filter.CustomerWaIds.ToArray();
            var customerIds = filter.CustomerIds.ToArray();
            query = query.Where(conversation =>
                (conversation.ProfileName != null && EF.Functions.ILike(conversation.ProfileName, pattern, "\\"))
                || (conversation.Username != null && EF.Functions.ILike(conversation.Username, pattern, "\\"))
                || (digitsPattern != null && conversation.WaId != null && EF.Functions.Like(conversation.WaId, digitsPattern))
                || (conversation.CustomerId != null && customerIds.Contains(conversation.CustomerId.Value))
                || (conversation.CustomerId == null && conversation.WaId != null && phones.Contains(conversation.WaId)));
        }

        // Comparación de fila de Postgres, el mismo orden que el índice, como el hilo de mensajes (MessageQueries).
        if (after is { } cursor)
        {
            query = filter.Sort == ConversationSort.Oldest
                ? query.Where(conversation => EF.Functions.GreaterThan(
                    ValueTuple.Create(conversation.LastActivityAt, conversation.Id), ValueTuple.Create(cursor.LastActivityAt, cursor.Id)))
                : query.Where(conversation => EF.Functions.LessThan(
                    ValueTuple.Create(conversation.LastActivityAt, conversation.Id), ValueTuple.Create(cursor.LastActivityAt, cursor.Id)));
        }

        // El mismo índice (IX_conversations_tenant_status_activity) sirve a los dos órdenes, leído al revés.
        var ordered = filter.Sort == ConversationSort.Oldest
            ? query.OrderBy(conversation => conversation.LastActivityAt).ThenBy(conversation => conversation.Id)
            : query.OrderByDescending(conversation => conversation.LastActivityAt).ThenByDescending(conversation => conversation.Id);
        return await ordered
            .Take(take)
            .Select(Projection)
            .ToListAsync(cancellationToken);
    }

    public async Task<ConversationCountsDto> CountsAsync(Guid tenantId, Guid? memberId, CancellationToken cancellationToken)
    {
        // Una sola sentencia con count(*) FILTER (WHERE …) en vez de seis consultas: con la base en otro nodo, un viaje gana a
        // seis aunque cada uno fuera más barato. D-A10: mine y unassigned cuentan sólo abiertas. Todo agregado exige abierta o
        // con no leídos, así que el WHERE externo pide lo mismo: el resultado no cambia y el planificador puede unir con un
        // BitmapOr los parciales IX_conversations_tenant_open e IX_conversations_tenant_unread, en vez de recorrer todas las
        // conversaciones del tenant (resueltas e historial importado incluidos).
        var counts = await dbContext.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId
                && (conversation.Status == ConversationStatus.Open || conversation.UnreadCount > 0))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Open = group.Count(conversation => conversation.Status == ConversationStatus.Open),
                Unread = group.Sum(conversation => conversation.UnreadCount > 0 ? conversation.UnreadCount : 0),
                Mine = group.Count(conversation => memberId != null && conversation.AssignedMemberId == memberId && conversation.Status == ConversationStatus.Open),
                Unassigned = group.Count(conversation => conversation.AssignedMemberId == null && conversation.Status == ConversationStatus.Open),
                Automated = group.Count(conversation => conversation.Status == ConversationStatus.Open
                    && conversation.AutomationStatus != ConversationAutomationStatus.None),
                AutomationActive = group.Count(conversation => conversation.Status == ConversationStatus.Open
                    && conversation.AutomationStatus == ConversationAutomationStatus.Active),
            })
            .SingleOrDefaultAsync(cancellationToken);

        // Un tenant sin conversaciones no tiene grupo: el agregado no devuelve fila.
        return counts is null
            ? new ConversationCountsDto(0, 0, 0, 0, 0, 0)
            : new ConversationCountsDto(counts.Open, counts.Unread, counts.Mine, counts.Unassigned, counts.Automated, counts.AutomationActive);
    }

    public Task<ConversationRow?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        dbContext.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId && conversation.Id == conversationId)
            .Select(Projection)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> HasResolvedEventSinceAsync(Guid tenantId, Guid conversationId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        // Un evento es una fila de messages con direction = 3 y kind = 13 (ConversationEventRows); su tipo viaja en details->>'type'.
        var utcSince = since.ToUniversalTime();
        var found = await dbContext.Database.SqlQuery<int>(
            $"""
            SELECT 1 AS "Value" FROM messaging.messages
            WHERE tenant_id = {tenantId} AND conversation_id = {conversationId} AND direction = 3 AND kind = 13
              AND details->>'type' = 'Resolved' AND occurred_at > {utcSince}
            LIMIT 1
            """).ToListAsync(cancellationToken);
        return found.Count > 0;
    }

    public async Task<bool> HasAdvisorEventSinceAsync(Guid tenantId, Guid conversationId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        // Spec 2026-11-01 D-5: mismo mecanismo que HasResolvedEventSinceAsync. Released no cuenta: siempre viene después de uno de estos.
        var utcSince = since.ToUniversalTime();
        var found = await dbContext.Database.SqlQuery<int>(
            $"""
            SELECT 1 AS "Value" FROM messaging.messages
            WHERE tenant_id = {tenantId} AND conversation_id = {conversationId} AND direction = 3 AND kind = 13
              AND details->>'type' IN ('Taken', 'AutoTaken', 'Transferred') AND occurred_at > {utcSince}
            LIMIT 1
            """).ToListAsync(cancellationToken);
        return found.Count > 0;
    }

    public async Task<IReadOnlyList<ConversationRow>> FindManyAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.ToArray();
        return await dbContext.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId && wanted.Contains(conversation.Id))
            .Select(Projection)
            .ToListAsync(cancellationToken);
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
