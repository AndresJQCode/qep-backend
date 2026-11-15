using BuildingBlocks.Application;
using FluentValidation;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;

namespace Modules.Ai.Application;

// Spec 2026-10-29 §5 y plan 2026-11-12 Tarea 9 (AI-B8): la actividad del agente en una conversación, el historial de sesiones y los conteos de
// la lista. Backend for frontend: cada respuesta viaja con su proveedor, los modelos que de verdad respondieron y sus fuentes ya resueltas a
// algo que se lee (la pregunta frecuente, el atajo de la respuesta rápida, el nombre de la herramienta), para que la pantalla no tenga que
// cruzar la versión del agente ni las respuestas rápidas.

/// <summary>Plan 2026-11-12 C-4: el consumo de un proveedor. La caché va <c>null</c> cuando el proveedor no la reporta (OpenAI no reporta la
/// creación), nunca <c>0</c>: un cero diría que no hubo caché cuando el dato no existe. Por proveedor, porque cada uno se contrasta con su
/// factura.</summary>
public sealed record AgentUsageDto(
    string Provider, int Calls, long InputTokens, long OutputTokens, long? CacheCreationInputTokens, long? CacheReadInputTokens);

/// <summary>Spec 2026-10-29 §5: lo del día del tenant (su reloj), de las sesiones que empezaron hoy.</summary>
/// <param name="Handled">Conversaciones que el agente empezó a atender.</param>
/// <param name="HandedOff">De ésas, las que terminaron en manos de una persona por el agente: traspaso pedido, tope, falla o ventana cerrada.
/// Una que una persona tomó por su cuenta (<c>Interrupted</c>) no cuenta: el agente no la soltó.</param>
/// <param name="QuotationDrafts">Borradores de cotización que el agente creó hoy (§8.2), sin los del playground.</param>
public sealed record AgentTodayDto(int Handled, int HandedOff, int QuotationDrafts)
{
    public static AgentTodayDto None { get; } = new(0, 0, 0);
}

/// <summary>Una sesión del historial (spec §5 <c>GET /sessions</c>): estado, motivo de fin, respuestas y consumo.</summary>
/// <param name="Replies">Respuestas enviadas (una por trabajo, aunque se haya partido en varios mensajes).</param>
/// <param name="Usage">Por proveedor (C-4); una sesión usa un solo proveedor (C-17), así que es a lo sumo una entrada.</param>
public sealed record AgentSessionDto(
    Guid Id,
    Guid AgentId,
    string AgentName,
    Guid ConversationId,
    int VersionNumber,
    string Provider,
    string Model,
    string Status,
    string? EndReason,
    string? FailureCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int Replies,
    IReadOnlyList<AgentUsageDto> Usage);

public sealed record AgentSessionListResponse(IReadOnlyList<AgentSessionDto> Items);

/// <summary>Una fuente de una respuesta, ya resuelta. <see cref="Kind"/> es <c>faq</c>, <c>quickReply</c> o <c>tool</c>.</summary>
/// <param name="Label">La pregunta, el atajo o el nombre de la herramienta; <c>null</c> si la respuesta rápida ya no existe.</param>
public sealed record AgentActivitySourceDto(string Kind, string Id, string? Label);

/// <summary>Lo que hizo un trabajo del agente: una respuesta enviada, un traspaso o un descarte (D-A14). Los descartados se muestran también,
/// porque gastaron tokens (D-A15).</summary>
/// <param name="Outcome">El de <c>ai.model_jobs.outcome</c> (<c>replied</c>, <c>handed_off</c>, <c>superseded</c>…).</param>
/// <param name="MessageIds">Los mensajes de Mensajería que salieron, en orden (una respuesta larga se parte, §6.3).</param>
/// <param name="Provider">El de la versión de la sesión (C-17).</param>
/// <param name="ServedModels">Los modelos que respondieron las llamadas del trabajo, sin repetir (C-4: con el respaldo de C-9 puede no ser el
/// pedido).</param>
public sealed record AgentActivityResponseDto(
    Guid JobId,
    string Status,
    string? Outcome,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<Guid> MessageIds,
    bool Truncated,
    IReadOnlyList<AgentActivitySourceDto> Sources,
    IReadOnlyList<AgentTestToolCallDto> ToolCalls,
    string Provider,
    IReadOnlyList<string> ServedModels,
    AgentUsageDto? Usage);

public sealed record AgentActivitySessionDto(
    Guid Id,
    Guid AgentId,
    string AgentName,
    int VersionNumber,
    string Provider,
    string Model,
    string Status,
    string? EndReason,
    string? FailureCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<AgentActivityResponseDto> Responses);

/// <summary>Las sesiones del agente en una conversación, de la más vieja a la más nueva. Vacía si ningún agente la atendió (o si es de otro
/// tenant: no se confirma que exista).</summary>
public sealed record AgentActivityDto(Guid ConversationId, IReadOnlyList<AgentActivitySessionDto> Sessions);

/// <summary>Los cortes del reloj del tenant: inicio del mes y del día, y fin del día.</summary>
public sealed record AgentActivityWindow(DateTimeOffset MonthStart, DateTimeOffset DayStart, DateTimeOffset DayEnd);

/// <param name="UsageByAgent">El mes por agente y proveedor; incluye el playground, que también gasta la clave del tenant (§7).</param>
public sealed record AgentListActivity(
    IReadOnlyDictionary<Guid, IReadOnlyList<AgentUsageDto>> UsageByAgent, IReadOnlyDictionary<Guid, AgentTodayDto> TodayByAgent);

/// <summary>Un trabajo de una sesión tal como lo guarda Ai; las fuentes todavía son ids (las resuelve el handler).</summary>
public sealed record AgentConversationJob(
    Guid Id,
    string Status,
    string? Outcome,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<Guid> MessageIds,
    bool Truncated,
    IReadOnlyList<string> SourceIds,
    IReadOnlyList<AgentTestToolCallDto> ToolCalls,
    IReadOnlyList<string> ServedModels,
    AgentUsageDto? Usage);

/// <param name="ConfigJson">El <c>config</c> de la versión de la sesión: las preguntas frecuentes que pudo citar.</param>
public sealed record AgentConversationSession(
    Guid Id,
    Guid AgentId,
    string AgentName,
    int VersionNumber,
    string Provider,
    string Model,
    string ConfigJson,
    string Status,
    string? EndReason,
    string? FailureCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<AgentConversationJob> Jobs);

/// <summary>Las lecturas de la actividad (EF en Infrastructure). Todas filtran por tenant.</summary>
public interface IAgentActivityReader
{
    Task<AgentListActivity> SummarizeAsync(Guid tenantId, AgentActivityWindow window, CancellationToken cancellationToken);

    /// <summary>Las sesiones más recientes primero, a lo sumo <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<AgentSessionDto>> ListSessionsAsync(
        Guid tenantId, Guid? agentId, DateTimeOffset? startedFrom, DateTimeOffset? startedBefore, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentConversationSession>> LoadConversationAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);
}

public sealed record GetConversationAgentActivityQuery(Guid TenantId, Guid ConversationId) : IQuery<AgentActivityDto>;

/// <param name="From">Inclusivo, sobre el inicio de la sesión.</param>
/// <param name="To">Exclusivo.</param>
public sealed record ListAgentSessionsQuery(Guid TenantId, Guid? AgentId, DateTimeOffset? From, DateTimeOffset? To)
    : IQuery<AgentSessionListResponse>;

public sealed class ListAgentSessionsValidator : AbstractValidator<ListAgentSessionsQuery>
{
    public ListAgentSessionsValidator() =>
        RuleFor(query => query.To)
            .GreaterThan(query => query.From)
            .When(query => query.From is not null && query.To is not null)
            .WithMessage("To must be after From.");
}

/// <summary>
/// Spec §5 <c>GET /conversations/{id}/agent-activity</c>. Permiso <c>ai.agent.read</c>, como el resto del módulo: si también lo ve todo asesor
/// que ve la conversación es <c>DECISIÓN-PENDIENTE P-10</c>.
/// </summary>
public sealed class GetConversationAgentActivityHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IAgentActivityReader reader, IAiQuickReplies quickReplies)
    : IQueryHandler<GetConversationAgentActivityQuery, AgentActivityDto>
{
    public const string FaqSource = "faq";
    public const string QuickReplySource = "quickReply";
    public const string ToolSource = "tool";

    public async Task<AgentActivityDto> HandleAsync(GetConversationAgentActivityQuery query, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.AgentRead, cancellationToken);
        var sessions = await reader.LoadConversationAsync(query.TenantId, query.ConversationId, cancellationToken);
        if (sessions.Count == 0)
        {
            return new AgentActivityDto(query.ConversationId, []);
        }

        // Las respuestas rápidas son las de hoy: una que se borró después sigue citada, sin atajo.
        var shortcuts = sessions.Any(session => session.Jobs.Any(job => job.SourceIds.Count > 0))
            ? (await quickReplies.ListAsync(query.TenantId, cancellationToken)).ToDictionary(reply => reply.Id, reply => reply.Shortcut, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        return new AgentActivityDto(
            query.ConversationId,
            [
                .. sessions.Select(session =>
                {
                    var faq = AgentConfig.FromJson(session.ConfigJson).Faq.ToDictionary(entry => entry.Id, entry => entry.Question, StringComparer.Ordinal);
                    return new AgentActivitySessionDto(
                        session.Id, session.AgentId, session.AgentName, session.VersionNumber, session.Provider, session.Model, session.Status,
                        session.EndReason, session.FailureCode, session.StartedAt, session.EndedAt,
                        [
                            .. session.Jobs.Select(job => new AgentActivityResponseDto(
                                job.Id, job.Status, job.Outcome, job.ErrorCode, job.CreatedAt, job.CompletedAt, job.MessageIds, job.Truncated,
                                [.. job.SourceIds.Select(id => Resolve(id, faq, shortcuts, job.ToolCalls))],
                                job.ToolCalls, session.Provider, job.ServedModels, job.Usage)),
                        ]);
                }),
            ]);
    }

    /// <summary>§8.2: una fuente es una pregunta frecuente de la versión, una llamada a herramienta del mismo trabajo o una respuesta rápida. El
    /// bucle ya descartó las que no existían al responder; lo que no se reconoce ahora es una respuesta rápida borrada después.</summary>
    private static AgentActivitySourceDto Resolve(
        string id, Dictionary<string, string> faq, Dictionary<string, string> shortcuts, IReadOnlyList<AgentTestToolCallDto> calls)
    {
        if (faq.TryGetValue(id, out var question))
        {
            return new AgentActivitySourceDto(FaqSource, id, question);
        }

        var call = calls.FirstOrDefault(candidate => candidate.CallId == id);
        if (call is not null)
        {
            return new AgentActivitySourceDto(ToolSource, id, call.Tool);
        }

        return new AgentActivitySourceDto(QuickReplySource, id, shortcuts.GetValueOrDefault(id));
    }
}

/// <summary>Spec §5 <c>GET /sessions?agentId=&amp;from=&amp;to=</c>: el historial, de la más reciente a la más vieja.</summary>
public sealed class ListAgentSessionsHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IAgentActivityReader reader, IValidator<ListAgentSessionsQuery> validator)
    : IQueryHandler<ListAgentSessionsQuery, AgentSessionListResponse>
{
    /// <summary>El spec no pagina el historial; para que un rango largo no traiga todo, se devuelven las más recientes hasta este tope.</summary>
    public const int MaxSessions = 200;

    public async Task<AgentSessionListResponse> HandleAsync(ListAgentSessionsQuery query, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.AgentRead, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        return new AgentSessionListResponse(
            await reader.ListSessionsAsync(query.TenantId, query.AgentId, query.From, query.To, MaxSessions, cancellationToken));
    }
}

/// <summary>El mes y el día del tenant, en su reloj (spec §5: «el mes del tenant»).</summary>
public static class AgentActivityWindows
{
    public static AgentActivityWindow For(TenantCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var today = calendar.Today;
        return new AgentActivityWindow(
            calendar.StartOfDayUtc(new DateOnly(today.Year, today.Month, 1)), calendar.StartOfDayUtc(today), calendar.EndOfDayExclusiveUtc(today));
    }
}
