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
/// <param name="Appointments">Spec 2026-11-23 §9.5: citas que el agente reservó hoy (<c>book_appointment</c> con <c>alreadyBooked: false</c>),
/// sin las repetidas ni las del playground.</param>
public sealed record AgentTodayDto(int Handled, int HandedOff, int QuotationDrafts, int Appointments)
{
    public static AgentTodayDto None { get; } = new(0, 0, 0, 0);
}

/// <summary>Una sesión del historial (spec §5 <c>GET /sessions</c>): estado, motivo de fin, respuestas y consumo.</summary>
/// <param name="Replies">Respuestas enviadas (una por trabajo, aunque se haya partido en varios mensajes).</param>
/// <param name="Usage">Por proveedor (C-4); una sesión usa un solo proveedor (C-17), así que es a lo sumo una entrada.</param>
/// <param name="Origin">Spec 2026-11-17 §7: <c>Connection</c> o <c>ChatbotBlock</c>, por nombre.</param>
/// <param name="Chatbot">Backend for frontend (§7): en una sesión de bloque, el chatbot que delegó con su nombre de ahora (para que el
/// historial diga «desde el chatbot X» sin que la pantalla pida los chatbots, que exigen otro permiso); <c>null</c> en las de conexión.</param>
/// <param name="Output">La salida por la que la sesión de bloque volvió al flujo (<c>done</c>, <c>human</c>, <c>noAnswer</c>, <c>idle</c>,
/// <c>error</c>); <c>null</c> mientras sigue o si terminó sin volver.</param>
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
    IReadOnlyList<AgentUsageDto> Usage,
    string Origin = nameof(AgentSessionOrigin.Connection),
    AgentSessionChatbotDto? Chatbot = null,
    string? Output = null);

/// <summary>Spec 2026-11-17 §7: el chatbot de una sesión de bloque. <see cref="Name"/> es <c>null</c> si el chatbot se borró.</summary>
public sealed record AgentSessionChatbotDto(Guid Id, string? Name);

/// <summary>Plan 2026-11-17 A5 (F-6): una página del historial, de la más reciente a la más vieja.</summary>
/// <param name="NextCursor">Lo que se manda en <c>cursor</c> para pedir la siguiente; <c>null</c> en la última.</param>
public sealed record AgentSessionListResponse(IReadOnlyList<AgentSessionDto> Items, string? NextCursor);

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

/// <summary>Una sesión en la actividad de la conversación.</summary>
/// <param name="Origin">Spec 2026-11-17 §7: <c>Connection</c> o <c>ChatbotBlock</c>, por nombre; lo mismo que <see cref="AgentSessionDto.Origin"/>.</param>
/// <param name="Chatbot">Backend for frontend: en una sesión de bloque, el chatbot que delegó con su nombre de ahora (<c>null</c> el nombre
/// si se borró), para que la conversación diga «desde el chatbot X» sin pedir los chatbots; <c>null</c> en las de conexión.</param>
/// <param name="Output">La salida por la que la sesión de bloque volvió al flujo (<c>done</c>, <c>human</c>, <c>noAnswer</c>, <c>idle</c>,
/// <c>error</c>); <c>null</c> mientras sigue o si terminó sin volver.</param>
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
    IReadOnlyList<AgentActivityResponseDto> Responses,
    string Origin = nameof(AgentSessionOrigin.Connection),
    AgentSessionChatbotDto? Chatbot = null,
    string? Output = null);

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
/// <param name="Origin">Spec 2026-11-17 §7: <c>Connection</c> o <c>ChatbotBlock</c>.</param>
/// <param name="ChatbotId">El chatbot que delegó, en una sesión de bloque; el nombre lo pone el handler (Ai no ve Automations).</param>
/// <param name="Output">La salida del bloque si la sesión volvió al flujo.</param>
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
    IReadOnlyList<AgentConversationJob> Jobs,
    string Origin = nameof(AgentSessionOrigin.Connection),
    Guid? ChatbotId = null,
    string? Output = null);

/// <summary>Las lecturas de la actividad (EF en Infrastructure). Todas filtran por tenant.</summary>
public interface IAgentActivityReader
{
    Task<AgentListActivity> SummarizeAsync(Guid tenantId, AgentActivityWindow window, CancellationToken cancellationToken);

    /// <summary>Las sesiones más recientes primero, a lo sumo <paramref name="take"/>, desde después de <paramref name="after"/> en el orden
    /// <c>(started_at, id)</c> descendente.</summary>
    /// <param name="startedFrom">Inclusivo.</param>
    /// <param name="startedBefore">Exclusivo.</param>
    Task<IReadOnlyList<AgentSessionDto>> ListSessionsAsync(
        Guid tenantId,
        Guid? agentId,
        DateTimeOffset? startedFrom,
        DateTimeOffset? startedBefore,
        AgentSessionCursor? after,
        int take,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentConversationSession>> LoadConversationAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);
}

public sealed record GetConversationAgentActivityQuery(Guid TenantId, Guid ConversationId) : IQuery<AgentActivityDto>;

/// <summary>Plan 2026-11-17 A5 (F-6): el historial por días del tenant, sobre el inicio de la sesión.</summary>
/// <param name="From">Día del tenant, inclusivo (desde su inicio en su zona horaria).</param>
/// <param name="To">Día del tenant, inclusivo (hasta su final en su zona horaria).</param>
/// <param name="Cursor">El <c>nextCursor</c> de la página anterior, tal cual.</param>
public sealed record ListAgentSessionsQuery(Guid TenantId, Guid? AgentId, DateOnly? From, DateOnly? To, string? Cursor)
    : IQuery<AgentSessionListResponse>;

public sealed class ListAgentSessionsValidator : AbstractValidator<ListAgentSessionsQuery>
{
    public ListAgentSessionsValidator()
    {
        RuleFor(query => query.To)
            .GreaterThanOrEqualTo(query => query.From)
            .When(query => query.From is not null && query.To is not null)
            .OverridePropertyName("to")
            .WithMessage("to must be on or after from.");
        RuleFor(query => query.Cursor)
            .Must(value => AgentSessionCursor.TryDecode(value, out _)).When(query => query.Cursor is not null)
            .OverridePropertyName("cursor")
            .WithMessage("cursor is not a cursor issued by this list.");
    }
}

/// <summary>
/// Spec §5 <c>GET /conversations/{id}/agent-activity</c>. Permiso <c>ai.agent.read</c>, como el resto del módulo: si también lo ve todo asesor
/// que ve la conversación es <c>DECISIÓN-PENDIENTE P-10</c>.
/// </summary>
public sealed class GetConversationAgentActivityHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IAgentActivityReader reader,
    IAiQuickReplies quickReplies,
    IAiChatbotNames chatbotNames)
    : IQueryHandler<GetConversationAgentActivityQuery, AgentActivityDto>
{
    public const string FaqSource = AgentSourceResolver.FaqSource;
    public const string QuickReplySource = AgentSourceResolver.QuickReplySource;
    public const string ToolSource = AgentSourceResolver.ToolSource;

    public async Task<AgentActivityDto> HandleAsync(GetConversationAgentActivityQuery query, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.AgentRead, cancellationToken);
        var sessions = await reader.LoadConversationAsync(query.TenantId, query.ConversationId, cancellationToken);
        if (sessions.Count == 0)
        {
            return new AgentActivityDto(query.ConversationId, []);
        }

        var shortcuts = await AgentSourceResolver.ShortcutsAsync(
            quickReplies, query.TenantId, sessions.Any(session => session.Jobs.Any(job => job.SourceIds.Count > 0)), cancellationToken);
        // Spec 2026-11-17 §7: el nombre de ahora de los chatbots, en una sola lectura y sólo si hay sesiones de bloque.
        Guid[] chatbots = [.. sessions.Select(session => session.ChatbotId).OfType<Guid>().Distinct()];
        var names = chatbots.Length == 0
            ? new Dictionary<Guid, string>()
            : await chatbotNames.ResolveAsync(query.TenantId, chatbots, cancellationToken);
        return new AgentActivityDto(
            query.ConversationId,
            [
                .. sessions.Select(session =>
                {
                    var faq = AgentSourceResolver.FaqOf(AgentConfig.FromJson(session.ConfigJson));
                    return new AgentActivitySessionDto(
                        session.Id, session.AgentId, session.AgentName, session.VersionNumber, session.Provider, session.Model, session.Status,
                        session.EndReason, session.FailureCode, session.StartedAt, session.EndedAt,
                        [
                            .. session.Jobs.Select(job => new AgentActivityResponseDto(
                                job.Id, job.Status, job.Outcome, job.ErrorCode, job.CreatedAt, job.CompletedAt, job.MessageIds, job.Truncated,
                                [.. job.SourceIds.Select(id => AgentSourceResolver.Resolve(id, faq, shortcuts, job.ToolCalls))],
                                job.ToolCalls, session.Provider, job.ServedModels, job.Usage)),
                        ],
                        session.Origin,
                        session.ChatbotId is { } chatbot ? new AgentSessionChatbotDto(chatbot, names.GetValueOrDefault(chatbot)) : null,
                        session.Output);
                }),
            ]);
    }
}

/// <summary>
/// Spec §5 y plan 2026-11-17 A5 <c>GET /sessions?agentId=&amp;from=&amp;to=&amp;cursor=</c>: el historial, de la más reciente a la más vieja,
/// paginado por cursor. Backend for frontend: <c>from</c> y <c>to</c> son días del tenant (su zona horaria la resuelve el backend, como en
/// <c>GetMessagingReportSummary</c>) y la pantalla pide la siguiente página con <c>nextCursor</c>, sin un tope silencioso que deje sesiones
/// afuera.
/// </summary>
public sealed class ListAgentSessionsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IAgentActivityReader reader,
    IValidator<ListAgentSessionsQuery> validator,
    ITenantClock tenantClock,
    IAiChatbotNames chatbotNames)
    : IQueryHandler<ListAgentSessionsQuery, AgentSessionListResponse>
{
    public const int PageSize = 50;

    public async Task<AgentSessionListResponse> HandleAsync(ListAgentSessionsQuery query, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.AgentRead, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        DateTimeOffset? startedFrom = null;
        DateTimeOffset? startedBefore = null;
        if (query.From is not null || query.To is not null)
        {
            var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);
            startedFrom = query.From is { } from ? calendar.StartOfDayUtc(from) : null;
            startedBefore = query.To is { } to ? calendar.EndOfDayExclusiveUtc(to) : null;
        }

        AgentSessionCursor? after = null;
        if (query.Cursor is not null && AgentSessionCursor.TryDecode(query.Cursor, out var decoded))
        {
            after = decoded;
        }

        // Una de más para saber si hay otra página sin contar el total.
        var rows = await reader.ListSessionsAsync(
            query.TenantId, query.AgentId, startedFrom, startedBefore, after, PageSize + 1, cancellationToken);
        IReadOnlyList<AgentSessionDto> page = rows.Count > PageSize ? [.. rows.Take(PageSize)] : rows;
        var nextCursor = rows.Count > PageSize
            ? AgentSessionCursor.Encode(new AgentSessionCursor(page[^1].StartedAt, page[^1].Id))
            : null;
        return new AgentSessionListResponse(await WithChatbotNamesAsync(query.TenantId, page, cancellationToken), nextCursor);
    }

    /// <summary>Spec 2026-11-17 §7: el nombre de ahora de los chatbots de la página, en una sola lectura.</summary>
    private async Task<IReadOnlyList<AgentSessionDto>> WithChatbotNamesAsync(
        Guid tenantId, IReadOnlyList<AgentSessionDto> page, CancellationToken cancellationToken)
    {
        Guid[] ids = [.. page.Select(session => session.Chatbot?.Id).OfType<Guid>().Distinct()];
        if (ids.Length == 0)
        {
            return page;
        }

        var names = await chatbotNames.ResolveAsync(tenantId, ids, cancellationToken);
        return
        [
            .. page.Select(session => session.Chatbot is { } chatbot
                ? session with { Chatbot = chatbot with { Name = names.GetValueOrDefault(chatbot.Id) } }
                : session),
        ];
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
