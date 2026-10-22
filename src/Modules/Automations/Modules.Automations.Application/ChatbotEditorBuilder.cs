using System.Text.Json;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Flow;
using Modules.Tenancy.Application;

namespace Modules.Automations.Application;

/// <summary>
/// Spec 2026-10-21 §4.4 (BFF): arma lo que el editor y la lista pintan sin encadenar llamados. Las conexiones de Mensajería, las «API
/// externa» y los horarios llegan por los puertos; el chatbot que ocupa cada conexión se resuelve listando los del tenant una sola vez
/// (nunca uno por conexión). El validador recibe el contexto real del chatbot (D-C2).
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
        var tenantConnections = await connections.ListAsync(chatbot.TenantId, cancellationToken);
        var scheduleNames = await businessHours.ScheduleNamesAsync(
            chatbot.TenantId, [.. tenantConnections.Select(connection => connection.Id)], cancellationToken);
        var apis = await externalApis.ListAsync(chatbot.TenantId, cancellationToken);
        var servedBy = await ServedByAsync(chatbot.TenantId, cancellationToken);
        var today = await TodayAsync(chatbot.TenantId, [chatbot.Id], cancellationToken);

        var flow = FlowJson.Parse(chatbot.DraftFlowJson);
        var issues = Validate(chatbot, flow, tenantConnections, apis, scheduleNames);
        var published = chatbot.PublishedVersionId is { } publishedId
            ? chatbot.Versions.FirstOrDefault(version => version.Id == publishedId)
            : null;

        return new ChatbotEditorDto(
            ToSummary(chatbot, tenantConnections, today),
            chatbot.Keywords,
            chatbot.RestartKeyword,
            chatbot.MaxAttempts,
            new ChatbotDraftDto(
                ToElement(chatbot.DraftFlowJson), ToElement(chatbot.DraftLayoutJson), chatbot.DraftUpdatedAt, chatbot.DraftUpdatedBy),
            published is null ? null : new ChatbotPublishedDto(published.Number, published.PublishedAt),
            issues,
            [.. tenantConnections.Select(connection => new SelectableConnectionDto(
                connection.Id,
                connection.Name,
                connection.Channel,
                connection.Handle,
                connection.Status,
                servedBy.GetValueOrDefault(connection.Id),
                scheduleNames.GetValueOrDefault(connection.Id)))],
            [.. apis.Select(api => new ExternalApiRefDto(api.Id, api.Name, api.Status))],
            FlowValidator.KnownVariables(flow));
    }

    /// <summary>La validación del borrador con el contexto del chatbot, sin armar el resto del editor (<c>/validate</c> y el
    /// guardado).</summary>
    public async Task<IReadOnlyList<FlowIssueDto>> ValidateAsync(Chatbot chatbot, FlowDefinition flow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatbot);
        var tenantConnections = await connections.ListAsync(chatbot.TenantId, cancellationToken);
        var scheduleNames = await businessHours.ScheduleNamesAsync(
            chatbot.TenantId, [.. chatbot.Connections.Select(connection => connection.ConnectionId)], cancellationToken);
        var apis = await externalApis.ListAsync(chatbot.TenantId, cancellationToken);
        return Validate(chatbot, flow, tenantConnections, apis, scheduleNames);
    }

    /// <summary>La lista (§4.4): una sola lectura de conexiones y de corridas para todos los chatbots.</summary>
    public async Task<ChatbotListResponse> BuildListAsync(Guid tenantId, IReadOnlyList<Chatbot> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var tenantConnections = await connections.ListAsync(tenantId, cancellationToken);
        var today = await TodayAsync(tenantId, [.. items.Select(chatbot => chatbot.Id)], cancellationToken);
        return new ChatbotListResponse([.. items.Select(chatbot => ToSummary(chatbot, tenantConnections, today))]);
    }

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

    /// <summary>D-C13: la conexión la ocupa el chatbot publicado (activo o en pausa) con la fila activa. Un tenant tiene pocos chatbots:
    /// leerlos todos una vez es más barato que preguntar conexión por conexión.</summary>
    private async Task<Dictionary<Guid, ServedByDto>> ServedByAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var servedBy = new Dictionary<Guid, ServedByDto>();
        foreach (var chatbot in await chatbots.ListAsync(tenantId, null, cancellationToken))
        {
            if (chatbot.PublishedVersionId is null)
            {
                continue;
            }

            foreach (var connection in chatbot.Connections.Where(connection => connection.Active))
            {
                servedBy[connection.ConnectionId] = new ServedByDto(chatbot.Id, chatbot.Name);
            }
        }

        return servedBy;
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

    private static ChatbotSummaryDto ToSummary(
        Chatbot chatbot, IReadOnlyList<AutomationConnectionRef> tenantConnections, IReadOnlyDictionary<Guid, (int Handled, int HandedOff)> today)
    {
        var own = chatbot.Connections.Select(connection => connection.ConnectionId).ToHashSet();
        var published = chatbot.PublishedVersionId is { } publishedId
            ? chatbot.Versions.FirstOrDefault(version => version.Id == publishedId)
            : null;
        var (handled, handedOff) = today.GetValueOrDefault(chatbot.Id);
        return new ChatbotSummaryDto(
            chatbot.Id,
            chatbot.Name,
            chatbot.Description,
            chatbot.Status.ToString(),
            chatbot.Trigger.ToString(),
            // Una conexión que Integrations ya borró no se muestra (allá el borrado es físico, D-H10).
            [.. tenantConnections.Where(connection => own.Contains(connection.Id))
                .Select(connection => new ChatbotConnectionDto(connection.Id, connection.Name, connection.Channel, connection.Handle))],
            published?.Number,
            published?.PublishedAt,
            chatbot.HasDraftChanges,
            new ChatbotTodayDto(handled, handedOff),
            chatbot.Version);
    }

    // El jsonb vuelve como objeto JSON, no como texto escapado: el editor lo lee tal cual.
    private static JsonElement ToElement(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
