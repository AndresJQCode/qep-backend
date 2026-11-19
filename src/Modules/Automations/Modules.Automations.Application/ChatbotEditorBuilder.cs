using System.Text.Json;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Flow;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Automations.Application;

/// <summary>
/// Spec 2026-10-21 §4.4 (BFF): arma lo que el editor y la lista pintan sin encadenar llamados. Las conexiones de Mensajería, las «API
/// externa» y los horarios llegan por los puertos; el chatbot que ocupa cada conexión sale de una sola proyección
/// (<see cref="IChatbotRepository.ListServingConnectionsAsync"/>), nunca uno por conexión. El validador recibe el contexto real del chatbot (D-C2).
/// </summary>
public sealed class ChatbotEditorBuilder(
    IAutomationConnections connections,
    IAutomationExternalApis externalApis,
    IAutomationMembers members,
    IAutomationBusinessHours businessHours,
    IConversationRunQueries runs,
    ITenantClock tenantClock,
    IChatbotConnectionSlots slots,
    IAutomationsAgentDirectory agents,
    ITenantModules tenantModules,
    IChatbotMediaStore media,
    IAutomationsCrmDirectory crmDirectory)
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
        // D-5: una sola lectura de miembros por request, para el selector y para validar los destinos.
        var assignable = await members.ListAsync(chatbot.TenantId, cancellationToken);
        var servedBy = await ServedByAsync(chatbot.TenantId, cancellationToken);
        var today = await TodayAsync(chatbot.TenantId, [chatbot.Id], cancellationToken);

        // Spec 2026-11-17 §7: el editor siempre trae los agentes (el selector del bloque), así que la misma lectura valida.
        var ai = await AiOfAsync(chatbot.TenantId, cancellationToken);
        // Spec 2026-11-19 (chatbot CRM action): the same for the «Acción de CRM» palette (tags and open stages): read once, validates too.
        var crm = await crmDirectory.ReadAsync(chatbot.TenantId, cancellationToken);

        var (flow, problem) = ParseStored(chatbot);
        var files = flow is null ? [] : await MediaOfAsync(chatbot.TenantId, flow, cancellationToken);
        var issues = flow is null ? [problem!] : Validate(chatbot, flow, tenantConnections, apis, assignable, scheduleNames, ai, files, crm);
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
            FlowValidator.KnownVariables(flow ?? EmptyFlow),
            chatbot.ExhaustedAction.ToString(),
            chatbot.ExhaustedMemberId,
            [.. assignable.Select(member => new ChatbotMemberDto(member.Id, member.Name))],
            [.. ai.Agents.Select(agent => new ChatbotEditorAgentDto(agent.Id, agent.Name, agent.Status, agent.Provider, agent.Model))],
            ai.Enabled,
            [.. files.Select(ChatbotMediaFileDto.From)],
            new ChatbotEditorCrmDto(
                [.. crm.Tags.Select(tag => new ChatbotEditorCrmRefDto(tag.Id, tag.Name))],
                [.. crm.OpenStages.Select(stage => new ChatbotEditorCrmRefDto(stage.Id, stage.Name))],
                crm.CustomersEnabled,
                crm.PipelineEnabled));
    }

    /// <summary>`GET /connections` (§4.4): las mismas conexiones que trae el editor, armadas por el mismo código.</summary>
    public async Task<IReadOnlyList<SelectableConnectionDto>> ListSelectableAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenantConnections = await connections.ListAsync(tenantId, cancellationToken);
        var scheduleNames = await businessHours.ScheduleNamesAsync(
            tenantId, [.. tenantConnections.Select(connection => connection.Id)], cancellationToken);
        return ToSelectable(tenantConnections, await ServedByAsync(tenantId, cancellationToken), scheduleNames);
    }

    /// <summary>Spec 2026-10-29 §4.3: quién ocupa cada conexión sale de la tabla neutral de Mensajería, así que también se ve un agente IA.</summary>
    private async Task<IReadOnlyDictionary<Guid, ServedByDto>> ServedByAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await slots.ListAsync(tenantId, cancellationToken))
            .ToDictionary(occupant => occupant.ConnectionId, occupant => new ServedByDto(occupant.Id, occupant.Name, occupant.Kind, occupant.Id));

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
        var assignable = await members.ListAsync(chatbot.TenantId, cancellationToken);
        return Validate(
            chatbot, flow, tenantConnections, apis, assignable, scheduleNames, await AiOfAsync(chatbot.TenantId, flow, cancellationToken),
            await MediaOfAsync(chatbot.TenantId, flow, cancellationToken), await CrmOfAsync(chatbot.TenantId, flow, cancellationToken));
    }

    /// <summary>Spec 2026-11-19 (chatbot CRM action): the tenant's tags and open stages, read only when the flow has an «Acción de CRM» block
    /// (as <see cref="AiOfAsync(Guid, FlowDefinition, CancellationToken)"/>): saving any other chatbot does not pay the reads.</summary>
    private async Task<CrmCatalog> CrmOfAsync(Guid tenantId, FlowDefinition flow, CancellationToken cancellationToken) =>
        flow.Nodes.Any(node => node is CrmActionNode) ? await crmDirectory.ReadAsync(tenantId, cancellationToken) : NoCrm;

    private static readonly CrmCatalog NoCrm = new(false, false, [], []);

    /// <summary>Spec 2026-11-18: los archivos de los bloques «Mensaje», en una sola lectura y sólo si el flujo tiene alguno (como <see cref="AiOfAsync(Guid, FlowDefinition, CancellationToken)"/>).
    /// La resolución es por tenant: un id de otro tenant (un flujo pegado o importado) no vuelve y el validador lo marca.</summary>
    private async Task<IReadOnlyList<ChatbotMediaFile>> MediaOfAsync(Guid tenantId, FlowDefinition flow, CancellationToken cancellationToken)
    {
        var fileIds = FlowMedia.FileIdsOf(flow);
        return fileIds.Count == 0 ? [] : await media.ResolveAsync(tenantId, fileIds, cancellationToken);
    }

    /// <summary>Spec 2026-11-17 §5.2: el módulo <c>ai</c> y los agentes del tenant, que sólo pregunta un flujo con un bloque «Agente IA»: el resto
    /// de los chatbots no paga la lectura de Ai ni de Tenancy.</summary>
    private async Task<AiContext> AiOfAsync(Guid tenantId, FlowDefinition flow, CancellationToken cancellationToken) =>
        flow.Nodes.Any(node => node is AiAgentNode) ? await AiOfAsync(tenantId, cancellationToken) : AiContext.None;

    private async Task<AiContext> AiOfAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // null = el stub de desarrollo: no bloquea, como TenantModuleGuard y AutomationsModuleStatus.
        var set = await tenantModules.FindAsync(tenantId, cancellationToken);
        var enabled = set is null || set.IsEnabled(TenantModuleKeys.Ai);
        return new AiContext(enabled, enabled ? await agents.ListAsync(tenantId, cancellationToken) : []);
    }

    /// <summary>Spec 2026-11-17 §7: sin el módulo <c>ai</c> no viaja ningún agente.</summary>
    private sealed record AiContext(bool Enabled, IReadOnlyList<AutomationAgentRef> Agents)
    {
        public static readonly AiContext None = new(false, []);
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
        var usage = await AiUsageThisMonthAsync(tenantId, rows.Count, cancellationToken);
        return new ChatbotListResponse([
            .. rows.Select(row => ToSummary(row, tenantConnections, today) with
            {
                AiUsageThisMonth = usage.TryGetValue(row.Id, out var tokens) ? new ChatbotAiUsageDto(tokens.InputTokens, tokens.OutputTokens) : null,
            }),
        ]);
    }

    /// <summary>Spec 2026-11-17 D-B15: «este mes» es el del reloj del tenant, como «hoy» (§4.4), no el de UTC.</summary>
    private async Task<IReadOnlyDictionary<Guid, (long InputTokens, long OutputTokens)>> AiUsageThisMonthAsync(
        Guid tenantId, int rows, CancellationToken cancellationToken)
    {
        if (rows == 0)
        {
            return new Dictionary<Guid, (long, long)>();
        }

        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        var first = new DateOnly(calendar.Today.Year, calendar.Today.Month, 1);
        return await agents.UsageByChatbotAsync(
            tenantId, calendar.StartOfDayUtc(first), calendar.StartOfDayUtc(first.AddMonths(1)), cancellationToken);
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

    private static List<FlowIssueDto> Validate(
        Chatbot chatbot,
        FlowDefinition flow,
        IReadOnlyList<AutomationConnectionRef> tenantConnections,
        IReadOnlyList<ExternalApiRef> apis,
        IReadOnlyList<AutomationMemberRef> assignable,
        IReadOnlyDictionary<Guid, string?> scheduleNames,
        AiContext ai,
        IReadOnlyList<ChatbotMediaFile> files,
        CrmCatalog crm)
    {
        var own = chatbot.Connections.Select(connection => connection.ConnectionId).ToHashSet();
        var channels = tenantConnections.Where(connection => own.Contains(connection.Id)).Select(connection => connection.Channel).ToHashSet(StringComparer.Ordinal);
        if (channels.Count == 0)
        {
            channels.Add(DefaultChannel);
        }

        var replyingMembers = assignable.Select(member => member.Id).ToHashSet();
        var context = new FlowValidationContext(
            channels,
            apis.Where(api => api.Status == Active).Select(api => api.Id).ToHashSet(),
            apis.Where(api => api.Status == Paused).Select(api => api.Id).ToHashSet(),
            own.All(id => scheduleNames.GetValueOrDefault(id) is not null),
            replyingMembers,
            ai.Enabled,
            [.. ai.Agents.Select(agent => new FlowAgentRef(agent.Id, agent.Name, agent.Status))],
            files.ToDictionary(file => file.FileId, file => file.Kind),
            crm.CustomersEnabled,
            crm.PipelineEnabled,
            crm.Tags.Select(tag => tag.Id).ToHashSet(),
            crm.OpenStages.Select(stage => stage.Id).ToHashSet());
        var issues = FlowValidator.Validate(flow, context)
            .Select(issue => new FlowIssueDto(issue.Severity.ToString(), issue.Code, issue.NodeId, issue.Message))
            .ToList();

        // D-5: la regla de agotamiento es un ajuste del chatbot (como trigger.keywords_empty), no del flujo, así que la revisa este método
        // y no el validador: el editor, /validate y publicar la ven por el mismo camino.
        if (chatbot.ExhaustedAction == AttemptsExhaustedAction.Handoff
            && chatbot.ExhaustedMemberId is { } exhaustedMember
            && !replyingMembers.Contains(exhaustedMember))
        {
            issues.Add(new FlowIssueDto(
                nameof(FlowIssueSeverity.Error), FlowIssueCodes.ExhaustedMemberInvalid, null,
                "Al agotar los intentos, el chatbot asigna la conversación a alguien que ya no puede responder conversaciones: elige otro miembro en los ajustes."));
        }

        return issues;
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
