using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.Infrastructure.Persistence;

/// <summary>
/// Plan 2026-11-12 Tarea 9: las lecturas de la actividad, sin rastreo y siempre por tenant. Las sumas de tokens van en <c>bigint</c>, y la caché
/// suma <c>NULL</c> cuando ninguna llamada la reportó (C-4): <c>SUM</c> de sólo nulos es nulo, no cero.
/// </summary>
internal sealed class AgentActivityReader(AiDbContext dbContext) : IAgentActivityReader
{
    /// <summary>El resultado de una reserva nueva, no de una repetida (spec 2026-11-23 §9.5).</summary>
    private const string BookedForReal = """{"alreadyBooked":false}""";

    /// <summary>Las sesiones que terminaron en manos de una persona por el agente (los cuatro caminos de <c>HandOffAsync</c>).</summary>
    private static readonly AgentSessionStatus[] HandedOffStatuses =
        [AgentSessionStatus.HandedOff, AgentSessionStatus.Failed, AgentSessionStatus.WindowClosed];

    public async Task<AgentListActivity> SummarizeAsync(Guid tenantId, AgentActivityWindow window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        var usage = await dbContext.UsageRecords.AsNoTracking()
            // Plan fase 2, Parte C: el consumo del copiloto (sin agente) no suma en la lista de agentes; se muestra en su configuración.
            .Where(record => record.TenantId == tenantId && record.CalledAt >= window.MonthStart && record.AgentId != null)
            .GroupBy(record => new { record.AgentId, record.Provider })
            .Select(group => new
            {
                AgentId = group.Key.AgentId!.Value,
                Usage = new AgentUsageDto(
                    group.Key.Provider,
                    group.Count(),
                    group.Sum(record => (long)record.InputTokens),
                    group.Sum(record => (long)record.OutputTokens),
                    // C-4: null si ningún registro lo reporta (ver la nota en ListSessionsAsync).
                    group.Max(record => record.CacheCreationInputTokens) == null ? null : group.Sum(record => (long?)record.CacheCreationInputTokens),
                    group.Max(record => record.CacheReadInputTokens) == null ? null : group.Sum(record => (long?)record.CacheReadInputTokens)),
            })
            .ToListAsync(cancellationToken);

        var sessions = await dbContext.Sessions.AsNoTracking()
            .Where(session => session.TenantId == tenantId && session.StartedAt >= window.DayStart && session.StartedAt < window.DayEnd)
            .GroupBy(session => session.AgentId)
            .Select(group => new
            {
                AgentId = group.Key,
                Handled = group.Count(),
                HandedOff = group.Count(session => HandedOffStatuses.Contains(session.Status)),
            })
            .ToListAsync(cancellationToken);

        // Sólo las de canales reales: el trabajo de una sesión, no el de una prueba (las simuladas no escriben).
        var drafts = await (
                from call in dbContext.ToolCalls.AsNoTracking()
                join job in dbContext.ModelJobs.AsNoTracking() on call.JobId equals job.Id
                join session in dbContext.Sessions.AsNoTracking() on job.SessionId equals session.Id
                where call.TenantId == tenantId
                    && call.Tool == AgentToolNames.CreateQuotationDraft
                    && call.Outcome == AiRunOutcomes.Ok
                    && call.CreatedAt >= window.DayStart
                    && call.CreatedAt < window.DayEnd
                group call by session.AgentId into perAgent
                select new { AgentId = perAgent.Key, Count = perAgent.Count() })
            .ToListAsync(cancellationToken);

        // Spec 2026-11-23 §9.5: reservas que el agente hizo hoy. Una repetida (alreadyBooked: true) describe una cita de antes y no cuenta.
        // La columna es jsonb, que normaliza los espacios: se pregunta por contención (@>) y no por el texto del resultado.
        var bookings = await (
                from call in dbContext.ToolCalls.AsNoTracking()
                join job in dbContext.ModelJobs.AsNoTracking() on call.JobId equals job.Id
                join session in dbContext.Sessions.AsNoTracking() on job.SessionId equals session.Id
                where call.TenantId == tenantId
                    && call.Tool == AgentToolNames.BookAppointment
                    && call.Outcome == AiRunOutcomes.Ok
                    && call.CreatedAt >= window.DayStart
                    && call.CreatedAt < window.DayEnd
                    && EF.Functions.JsonContains(call.Result!, BookedForReal)
                group call by session.AgentId into perAgent
                select new { AgentId = perAgent.Key, Count = perAgent.Count() })
            .ToListAsync(cancellationToken);

        var draftsByAgent = drafts.ToDictionary(row => row.AgentId, row => row.Count);
        var bookingsByAgent = bookings.ToDictionary(row => row.AgentId, row => row.Count);
        var today = sessions.ToDictionary(
            row => row.AgentId,
            row => new AgentTodayDto(
                row.Handled, row.HandedOff, draftsByAgent.GetValueOrDefault(row.AgentId), bookingsByAgent.GetValueOrDefault(row.AgentId)));
        foreach (var agentId in draftsByAgent.Keys.Concat(bookingsByAgent.Keys).Distinct().Where(id => !today.ContainsKey(id)))
        {
            // Un borrador o una reserva de hoy en una sesión que empezó ayer.
            today[agentId] = new AgentTodayDto(0, 0, draftsByAgent.GetValueOrDefault(agentId), bookingsByAgent.GetValueOrDefault(agentId));
        }

        return new AgentListActivity(
            usage.GroupBy(row => row.AgentId).ToDictionary(
                group => group.Key, group => (IReadOnlyList<AgentUsageDto>)[.. group.Select(row => row.Usage).OrderBy(row => row.Provider, StringComparer.Ordinal)]),
            today);
    }

    public async Task<IReadOnlyList<AgentSessionDto>> ListSessionsAsync(
        Guid tenantId,
        Guid? agentId,
        DateTimeOffset? startedFrom,
        DateTimeOffset? startedBefore,
        AgentSessionCursor? after,
        int take,
        CancellationToken cancellationToken)
    {
        var sessions = dbContext.Sessions.AsNoTracking().Where(session => session.TenantId == tenantId);
        if (agentId is { } agent)
        {
            sessions = sessions.Where(session => session.AgentId == agent);
        }

        if (startedFrom is { } start)
        {
            sessions = sessions.Where(session => session.StartedAt >= start);
        }

        if (startedBefore is { } end)
        {
            sessions = sessions.Where(session => session.StartedAt < end);
        }

        // Plan 2026-11-17 A5: keyset (started_at, id) < (@at, @id), el mismo orden de la página; el id desempata dos sesiones del mismo
        // instante, así ninguna se repite ni se salta entre páginas (precedente ConversationQueries).
        if (after is { } cursor)
        {
            sessions = sessions.Where(session => EF.Functions.LessThan(
                ValueTuple.Create(session.StartedAt, session.Id), ValueTuple.Create(cursor.StartedAt, cursor.Id)));
        }

        var rows = await (
                from session in sessions
                join version in dbContext.AgentVersions.AsNoTracking() on session.AgentVersionId equals version.Id
                join owner in dbContext.Agents.AsNoTracking() on session.AgentId equals owner.Id
                orderby session.StartedAt descending, session.Id descending
                select new { Session = session, version.Number, version.Provider, version.Model, owner.Name })
            .Take(take)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(row => row.Session.Id).ToList();
        var replies = await dbContext.Turns.AsNoTracking()
            .Where(turn => ids.Contains(turn.SessionId) && turn.Role == AgentTurnRoleKind.Agent && turn.JobId != null)
            .GroupBy(turn => turn.SessionId)
            .Select(group => new { SessionId = group.Key, Replies = group.Select(turn => turn.JobId).Distinct().Count() })
            .ToDictionaryAsync(row => row.SessionId, row => row.Replies, cancellationToken);
        var usage = Group(await dbContext.UsageRecords.AsNoTracking()
            .Where(record => record.TenantId == tenantId && record.SessionId != null && ids.Contains(record.SessionId!.Value))
            .GroupBy(record => new { Key = record.SessionId!.Value, record.Provider })
            .Select(group => new UsageRow(
                group.Key.Key,
                group.Key.Provider,
                group.Count(),
                group.Sum(record => (long)record.InputTokens),
                group.Sum(record => (long)record.OutputTokens),
                // C-4: null si ningún registro lo reporta. Sum sobre long? se traduce con COALESCE(sum, 0) y daría un cero inventado;
                // max de puros null es null.
                group.Max(record => record.CacheCreationInputTokens) == null ? null : group.Sum(record => (long?)record.CacheCreationInputTokens),
                group.Max(record => record.CacheReadInputTokens) == null ? null : group.Sum(record => (long?)record.CacheReadInputTokens)))
            .ToListAsync(cancellationToken));

        return
        [
            .. rows.Select(row => new AgentSessionDto(
                row.Session.Id,
                row.Session.AgentId,
                row.Name,
                row.Session.ConversationId,
                row.Number,
                row.Provider,
                row.Model,
                row.Session.Status.ToString(),
                row.Session.EndReason,
                row.Session.FailureCode,
                row.Session.StartedAt,
                row.Session.EndedAt,
                replies.GetValueOrDefault(row.Session.Id),
                usage.GetValueOrDefault(row.Session.Id) ?? [],
                row.Session.Origin.ToString(),
                // Spec 2026-11-17 §7: el nombre lo pone el handler (Ai no ve Automations).
                row.Session.ChatbotId is { } chatbot ? new AgentSessionChatbotDto(chatbot, null) : null,
                row.Session.Status == AgentSessionStatus.Returned ? row.Session.EndReason : null)),
        ];
    }

    public async Task<IReadOnlyList<AgentConversationSession>> LoadConversationAsync(
        Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        var rows = await (
                from session in dbContext.Sessions.AsNoTracking()
                join version in dbContext.AgentVersions.AsNoTracking() on session.AgentVersionId equals version.Id
                join owner in dbContext.Agents.AsNoTracking() on session.AgentId equals owner.Id
                where session.TenantId == tenantId && session.ConversationId == conversationId
                orderby session.StartedAt, session.Id
                select new { Session = session, version.Number, version.Provider, version.Model, version.ConfigJson, owner.Name })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var sessionIds = rows.Select(row => row.Session.Id).ToList();
        var jobs = await dbContext.ModelJobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId && job.SessionId != null && sessionIds.Contains(job.SessionId!.Value))
            .OrderBy(job => job.CreatedAt).ThenBy(job => job.Id)
            .ToListAsync(cancellationToken);
        var jobIds = jobs.Select(job => job.Id).ToList();
        var turns = await dbContext.Turns.AsNoTracking()
            .Where(turn => sessionIds.Contains(turn.SessionId) && turn.Role == AgentTurnRoleKind.Agent && turn.JobId != null)
            .OrderBy(turn => turn.Seq)
            .ToListAsync(cancellationToken);
        var calls = await dbContext.ToolCalls.AsNoTracking()
            .Where(call => call.TenantId == tenantId && jobIds.Contains(call.JobId))
            .OrderBy(call => call.Id)
            .ToListAsync(cancellationToken);
        var records = dbContext.UsageRecords.AsNoTracking().Where(record => record.TenantId == tenantId && jobIds.Contains(record.JobId));
        var usage = Group(await records
            .GroupBy(record => new { Key = record.JobId, record.Provider })
            .Select(group => new UsageRow(
                group.Key.Key,
                group.Key.Provider,
                group.Count(),
                group.Sum(record => (long)record.InputTokens),
                group.Sum(record => (long)record.OutputTokens),
                // C-4: null si ningún registro lo reporta. Sum sobre long? se traduce con COALESCE(sum, 0) y daría un cero inventado;
                // max de puros null es null.
                group.Max(record => record.CacheCreationInputTokens) == null ? null : group.Sum(record => (long?)record.CacheCreationInputTokens),
                group.Max(record => record.CacheReadInputTokens) == null ? null : group.Sum(record => (long?)record.CacheReadInputTokens)))
            .ToListAsync(cancellationToken));
        var served = (await records
                .Where(record => record.ServedModel != null)
                .OrderBy(record => record.Id)
                .Select(record => new { record.JobId, record.ServedModel })
                .ToListAsync(cancellationToken))
            .GroupBy(record => record.JobId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)[.. group.Select(record => record.ServedModel!).Distinct(StringComparer.Ordinal)]);

        return
        [
            .. rows.Select(row => new AgentConversationSession(
                row.Session.Id,
                row.Session.AgentId,
                row.Name,
                row.Number,
                row.Provider,
                row.Model,
                row.ConfigJson,
                row.Session.Status.ToString(),
                row.Session.EndReason,
                row.Session.FailureCode,
                row.Session.StartedAt,
                row.Session.EndedAt,
                [
                    .. jobs.Where(job => job.SessionId == row.Session.Id).Select(job =>
                    {
                        var parts = turns.Where(turn => turn.JobId == job.Id).ToList();
                        return new AgentConversationJob(
                            job.Id,
                            job.Status.ToString(),
                            job.Outcome,
                            job.ErrorCode,
                            job.CreatedAt,
                            job.CompletedAt,
                            [.. parts.Select(turn => turn.MessageId).OfType<Guid>()],
                            parts.Any(turn => turn.Truncated),
                            // Las fuentes van en la primera parte de la respuesta (§6.3).
                            [.. parts.SelectMany(turn => JsonSerializer.Deserialize<string[]>(turn.SourcesJson) ?? []).Distinct(StringComparer.Ordinal)],
                            [
                                .. calls.Where(call => call.JobId == job.Id).Select(call => new AgentTestToolCallDto(
                                    call.CallId,
                                    call.Tool,
                                    JsonDocument.Parse(call.Arguments).RootElement.Clone(),
                                    call.Result is null ? null : JsonDocument.Parse(call.Result).RootElement.Clone(),
                                    call.Outcome,
                                    call.ErrorCode,
                                    call.DurationMs,
                                    call.CreatedAt)),
                            ],
                            served.GetValueOrDefault(job.Id) ?? [],
                            usage.GetValueOrDefault(job.Id)?.SingleOrDefault());
                    }),
                ],
                row.Session.Origin.ToString(),
                row.Session.ChatbotId,
                // La misma regla que el historial: la salida es el motivo de fin de una sesión que volvió al flujo.
                row.Session.Status == AgentSessionStatus.Returned ? row.Session.EndReason : null)),
        ];
    }

    /// <summary>Una fila de consumo agrupada por una clave (sesión o trabajo) y un proveedor.</summary>
    private sealed record UsageRow(
        Guid Key, string Provider, int Calls, long InputTokens, long OutputTokens, long? CacheCreation, long? CacheRead);

    private static Dictionary<Guid, IReadOnlyList<AgentUsageDto>> Group(IEnumerable<UsageRow> rows) =>
        rows.GroupBy(row => row.Key).ToDictionary(
            group => group.Key,
            group => (IReadOnlyList<AgentUsageDto>)
            [
                .. group
                    .OrderBy(row => row.Provider, StringComparer.Ordinal)
                    .Select(row => new AgentUsageDto(row.Provider, row.Calls, row.InputTokens, row.OutputTokens, row.CacheCreation, row.CacheRead)),
            ]);
}
