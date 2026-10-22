using System.Text.Json;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Flow;
using Modules.Tenancy.Application;

namespace Modules.Automations.Application;

/// <summary>
/// Spec 2026-10-21 §4.4 (BFF): arma lo que el editor y la lista pintan sin encadenar llamados. Las conexiones de Mensajería, las «API
/// externa» y los horarios llegan por los puertos; el chatbot que ocupa cada conexión sale de una sola proyección
/// (<see cref="IChatbotRepository.ListServingConnectionsAsync"/>), nunca uno por conexión. El validador recibe el contexto real del chatbot (D-C2).
/// </summary>
public sealed class ChatbotEditorBuilder(
    IAutomationConnections connections,
    IAutomationExternalApis externalApis,
    IAutomationBusinessHours businessHours,
    IChatbotRepository chatbots,
    IConversationRunQueries runs,
    ITenantClock tenantClock)
{
    /// <summary>Canal que se supone mientras el chatbot no tiene conexiones: hoy el motor sólo atiende WhatsApp (D-M6).</summary>
    private const string DefaultChannel = "WhatsApp";

    private const string Active = "Active";
    private const string Paused = "Paused";

    public async Task<ChatbotEditorDto> BuildAsync(Chatbot chatbot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatbot);
        return await BuildAsync(chatbot, await connections.ListAsync(chatbot.TenantId, cancellationToken), cancellationToken);
    }

    /// <summary>Con las conexiones del tenant ya leídas (crear y editar las leen para revisarlas): una sola lectura por request.</summary>
    public async Task<ChatbotEditorDto> BuildAsync(
        Chatbot chatbot, IReadOnlyList<AutomationConnectionRef> tenantConnections, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatbot);
        ArgumentNullException.ThrowIfNull(tenantConnections);
        var scheduleNames = await businessHours.ScheduleNamesAsync(
            chatbot.TenantId, [.. tenantConnections.Select(connection => connection.Id)], cancellationToken);
        var apis = await externalApis.ListAsync(chatbot.TenantId, cancellationToken);
        var servedBy = await ServedByAsync(chatbot.TenantId, cancellationToken);
        var today = await TodayAsync(chatbot.TenantId, [chatbot.Id], cancellationToken);

        var (flow, problem) = ParseStored(chatbot);
        var issues = flow is null ? [problem!] : Validate(chatbot, flow, tenantConnections, apis, scheduleNames);
        var published = chatbot.PublishedVersionId is { } publishedId
            ? chatbot.Versions.FirstOrDefault(version => version.Id == publishedId)
            : null;

        return new ChatbotEditorDto(
            ToSummary(Row(chatbot), tenantConnections, today),
            chatbot.Keywords,
            chatbot.RestartKeyword,
            chatbot.MaxAttempts,
            new ChatbotDraftDto(
                ToElement(chatbot.DraftFlowJson), ToElement(chatbot.DraftLayoutJson), chatbot.DraftUpdatedAt, chatbot.DraftUpdatedBy),
            published is null ? null : new ChatbotPublishedDto(published.Number, published.PublishedAt),
            issues,
            ToSelectable(tenantConnections, servedBy, scheduleNames),
            [.. apis.Select(api => new ExternalApiRefDto(api.Id, api.Name, api.Status))],
            FlowValidator.KnownVariables(flow ?? EmptyFlow));
    }

    /// <summary>`GET /connections` (§4.4): las mismas conexiones que trae el editor, armadas por el mismo código.</summary>
    public async Task<IReadOnlyList<SelectableConnectionDto>> ListSelectableAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenantConnections = await connections.ListAsync(tenantId, cancellationToken);
        var scheduleNames = await businessHours.ScheduleNamesAsync(
            tenantId, [.. tenantConnections.Select(connection => connection.Id)], cancellationToken);
        return ToSelectable(tenantConnections, await ServedByAsync(tenantId, cancellationToken), scheduleNames);
    }

    private async Task<IReadOnlyDictionary<Guid, ServedByDto>> ServedByAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await chatbots.ListServingConnectionsAsync(tenantId, cancellationToken))
            .GroupBy(serving => serving.ConnectionId)
            .ToDictionary(group => group.Key, group => new ServedByDto(group.First().ChatbotId, group.First().Name));

    private static IReadOnlyList<SelectableConnectionDto> ToSelectable(
        IReadOnlyList<AutomationConnectionRef> tenantConnections,
        IReadOnlyDictionary<Guid, ServedByDto> servedBy,
        IReadOnlyDictionary<Guid, string?> scheduleNames) =>
        [.. tenantConnections.Select(connection => new SelectableConnectionDto(
            connection.Id,
            connection.Name,
            connection.Channel,
            connection.Handle,
            connection.Status,
            servedBy.GetValueOrDefault(connection.Id),
            scheduleNames.GetValueOrDefault(connection.Id)))];

    /// <summary>La validación de un flujo recién leído con el contexto del chatbot, sin armar el resto del editor (el guardado).</summary>
    public async Task<IReadOnlyList<FlowIssueDto>> ValidateAsync(Chatbot chatbot, FlowDefinition flow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatbot);
        return await ValidateAsync(chatbot, flow, await connections.ListAsync(chatbot.TenantId, cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyList<FlowIssueDto>> ValidateAsync(
        Chatbot chatbot, FlowDefinition flow, IReadOnlyList<AutomationConnectionRef> tenantConnections, CancellationToken cancellationToken)
    {
        var scheduleNames = await businessHours.ScheduleNamesAsync(
            chatbot.TenantId, [.. chatbot.Connections.Select(connection => connection.ConnectionId)], cancellationToken);
        var apis = await externalApis.ListAsync(chatbot.TenantId, cancellationToken);
        return Validate(chatbot, flow, tenantConnections, apis, scheduleNames);
    }

    /// <summary>La validación del borrador guardado (<c>/validate</c>): la misma que trae <see cref="BuildAsync(Chatbot, CancellationToken)"/>.</summary>
    public async Task<IReadOnlyList<FlowIssueDto>> ValidateStoredAsync(Chatbot chatbot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatbot);
        return await ValidateStoredAsync(chatbot, await connections.ListAsync(chatbot.TenantId, cancellationToken), cancellationToken);
    }

    /// <summary>Con las conexiones del tenant ya leídas: publicar valida y después arma el editor con la misma lectura.</summary>
    public async Task<IReadOnlyList<FlowIssueDto>> ValidateStoredAsync(
        Chatbot chatbot, IReadOnlyList<AutomationConnectionRef> tenantConnections, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatbot);
        ArgumentNullException.ThrowIfNull(tenantConnections);
        var (flow, problem) = ParseStored(chatbot);
        return flow is null ? [problem!] : await ValidateAsync(chatbot, flow, tenantConnections, cancellationToken);
    }

    /// <summary>La lista (§4.4): filas proyectadas y una sola lectura de conexiones y de corridas para todos los chatbots.</summary>
    public async Task<ChatbotListResponse> BuildListAsync(Guid tenantId, IReadOnlyList<ChatbotSummaryRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var tenantConnections = await connections.ListAsync(tenantId, cancellationToken);
        var today = await TodayAsync(tenantId, [.. rows.Select(row => row.Id)], cancellationToken);
        return new ChatbotListResponse([.. rows.Select(row => ToSummary(row, tenantConnections, today))]);
    }

    /// <summary>El borrador guardado siempre pasó por <see cref="FlowJson.Parse"/>, pero un dato viejo o un cambio de esquema puede
    /// dejarlo ilegible: se muestra como un error del flujo (no deja publicar) en vez de un 500 que deja el editor sin abrir, y el
    /// siguiente guardado lo reemplaza.</summary>
    private static (FlowDefinition? Flow, FlowIssueDto? Problem) ParseStored(Chatbot chatbot)
    {
        try
        {
            return (FlowJson.Parse(chatbot.DraftFlowJson), null);
        }
        catch (FlowFormatException exception)
        {
            return (null, new FlowIssueDto(nameof(FlowIssueSeverity.Error), exception.Code, null, exception.Message));
        }
    }

    private static readonly FlowDefinition EmptyFlow = new(FlowDefinition.CurrentSchema, new FlowStart(null), [], []);

    private static IReadOnlyList<FlowIssueDto> Validate(
        Chatbot chatbot,
        FlowDefinition flow,
        IReadOnlyList<AutomationConnectionRef> tenantConnections,
        IReadOnlyList<ExternalApiRef> apis,
        IReadOnlyDictionary<Guid, string?> scheduleNames)
    {
        var own = chatbot.Connections.Select(connection => connection.ConnectionId).ToHashSet();
        var channels = tenantConnections.Where(connection => own.Contains(connection.Id)).Select(connection => connection.Channel).ToHashSet(StringComparer.Ordinal);
        if (channels.Count == 0)
        {
            channels.Add(DefaultChannel);
        }

        var context = new FlowValidationContext(
            channels,
            apis.Where(api => api.Status == Active).Select(api => api.Id).ToHashSet(),
            apis.Where(api => api.Status == Paused).Select(api => api.Id).ToHashSet(),
            own.All(id => scheduleNames.GetValueOrDefault(id) is not null));
        return [.. FlowValidator.Validate(flow, context).Select(issue => new FlowIssueDto(issue.Severity.ToString(), issue.Code, issue.NodeId, issue.Message))];
    }

    /// <summary>§4.4: «hoy» es el día del tenant (su zona horaria), no el de UTC.</summary>
    private async Task<IReadOnlyDictionary<Guid, (int Handled, int HandedOff)>> TodayAsync(
        Guid tenantId, IReadOnlyCollection<Guid> chatbotIds, CancellationToken cancellationToken)
    {
        if (chatbotIds.Count == 0)
        {
            return new Dictionary<Guid, (int, int)>();
        }

        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        return await runs.TodayAsync(
            tenantId, chatbotIds, calendar.StartOfDayUtc(calendar.Today), calendar.EndOfDayExclusiveUtc(calendar.Today), cancellationToken);
    }

    private static ChatbotSummaryRow Row(Chatbot chatbot)
    {
        var published = chatbot.PublishedVersionId is { } publishedId
            ? chatbot.Versions.FirstOrDefault(version => version.Id == publishedId)
            : null;
        return new ChatbotSummaryRow(
            chatbot.Id, chatbot.Name, chatbot.Description, chatbot.Status, chatbot.Trigger,
            [.. chatbot.Connections.Select(connection => connection.ConnectionId)], published?.Number, published?.PublishedAt,
            chatbot.DraftUpdatedAt, chatbot.Version);
    }

    private static ChatbotSummaryDto ToSummary(
        ChatbotSummaryRow row, IReadOnlyList<AutomationConnectionRef> tenantConnections, IReadOnlyDictionary<Guid, (int Handled, int HandedOff)> today)
    {
        var own = row.ConnectionIds.ToHashSet();
        var (handled, handedOff) = today.GetValueOrDefault(row.Id);
        return new ChatbotSummaryDto(
            row.Id,
            row.Name,
            row.Description,
            row.Status.ToString(),
            row.Trigger.ToString(),
            // Una conexión que Integrations ya borró no se muestra (allá el borrado es físico, D-H10).
            [.. tenantConnections.Where(connection => own.Contains(connection.Id))
                .Select(connection => new ChatbotConnectionDto(connection.Id, connection.Name, connection.Channel, connection.Handle))],
            row.PublishedNumber,
            row.PublishedAt,
            row.HasDraftChanges,
            new ChatbotTodayDto(handled, handedOff),
            row.Version);
    }

    // El jsonb vuelve como objeto JSON, no como texto escapado: el editor lo lee tal cual.
    private static JsonElement ToElement(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
