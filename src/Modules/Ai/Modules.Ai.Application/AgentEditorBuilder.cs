using Modules.Ai.Domain;

namespace Modules.Ai.Application;

/// <summary>Arma la lista y el editor de agentes, y la validación del borrador con lo que saben otros módulos (conexiones, miembros,
/// lista de modelos). Un solo lugar para que el <c>GET</c>, el guardado y la publicación digan lo mismo.</summary>
public sealed class AgentEditorBuilder(
    IAiConnections connections,
    IAgentConnectionSlots slots,
    IAiMembers members,
    IAiModelCatalog catalog,
    IAiQuotationAdvisors advisors,
    IAiProviderCredentials credentials,
    IAiQuotationBillingAccounts billingAccountOptions,
    IAiScheduling scheduling,
    ISystemAgentTemplateCatalog templates)
{
    private const string WhatsApp = "WhatsApp";

    private const string Instagram = "Instagram";

    /// <summary>Spec 2026-10-29 §6.5: WhatsApp e Instagram tienen salida de bot para un agente (Instagram sólo con texto).</summary>
    public static bool SupportsBot(string channel) => channel is WhatsApp or Instagram;

    public AgentModelAvailability AvailabilityOf(AgentModelRef model)
    {
        if (!AiProvider.TryParse(model.Provider, out var provider))
        {
            return AgentModelAvailability.UnknownProvider;
        }

        var listed = catalog.Providers.Single(entry => entry.Provider == provider).Models
            .FirstOrDefault(candidate => string.Equals(candidate.Id, model.Id, StringComparison.Ordinal));
        return listed is { Available: true } ? AgentModelAvailability.Available : AgentModelAvailability.Unavailable;
    }

    /// <summary>Los problemas del borrador con el contexto del agente. <paramref name="publishing"/> endurece el modelo (C-16).</summary>
    public async Task<IReadOnlyList<AgentIssue>> ValidateAsync(Agent agent, bool publishing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        // Sólo los ids: esto corre en cada autoguardado del borrador, y la lectura completa resuelve cada conexión activa.
        return await ValidateAsync(
            agent, publishing, await connections.ListIdsAsync(agent.TenantId, cancellationToken), billingAccounts: null, cancellationToken);
    }

    /// <summary>Sólo cuentan las conexiones que siguen existiendo en Integrations: Integrations borra la conexión sin avisar (nadie
    /// consume <c>integrations.connection.deleted</c>) y la referencia de <c>ai.agent_connections</c> es blanda. Sin el filtro, un agente
    /// cuya única conexión se borró pasaría la validación y se publicaría sin atender nada.</summary>
    /// <param name="billingAccounts">Las cuentas elegibles si ya se leyeron (el editor las lista igual); <c>null</c> las lee sólo si hacen
    /// falta.</param>
    private async Task<IReadOnlyList<AgentIssue>> ValidateAsync(
        Agent agent,
        bool publishing,
        IReadOnlySet<Guid> live,
        IReadOnlyList<AiBillingAccountOption>? billingAccounts,
        CancellationToken cancellationToken)
    {
        var config = agent.DraftConfig;
        bool? memberCanReply = null;
        if (config.Handoff.Destination == AgentHandoff.ToMember && config.Handoff.MemberId is { } memberId)
        {
            memberCanReply = (await members.ListAsync(agent.TenantId, cancellationToken)).Any(member => member.Id == memberId);
        }

        bool? advisorCanQuote = null;
        if (config.Tools.QuotationDraft && config.QuotationDraft?.AdvisorMemberId is { } advisorId)
        {
            advisorCanQuote = await advisors.CanQuoteAsync(agent.TenantId, advisorId, cancellationToken);
        }

        // Plan 2026-11-17 A3: la cuenta guardada se compara con las que el editor ofrece, no con una regla propia.
        bool? billingAccountEligible = null;
        if (config.Tools.QuotationDraft && config.QuotationDraft?.BillingAccount is { } account)
        {
            billingAccounts ??= await billingAccountOptions.ListAsync(agent.TenantId, cancellationToken);
            billingAccountEligible = billingAccounts.Any(option => option.Matches(account));
        }

        // Spec 2026-11-23 §9.2: la disponibilidad de la Agenda se lee sólo con la herramienta prendida (el autoguardado valida seguido).
        var schedulingReadiness = config.Tools.Scheduling ? await scheduling.GetReadinessAsync(agent.TenantId, cancellationToken) : null;

        return AgentConfigValidator.Validate(
            config,
            new AgentValidationContext(
                LiveConnectionIds(agent, live).Count,
                AvailabilityOf(config.Model),
                memberCanReply,
                publishing,
                advisorCanQuote,
                billingAccountEligible,
                schedulingReadiness));
    }

    private static List<Guid> LiveConnectionIds(Agent agent, IReadOnlySet<Guid> live) =>
        [.. agent.Connections.Select(connection => connection.ConnectionId).Where(live.Contains)];

    public async Task<AgentEditorDto> BuildAsync(Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        // Una sola lectura de las conexiones del tenant para la validación, las elegidas y las elegibles.
        var live = await connections.ListAsync(agent.TenantId, cancellationToken);
        var liveIds = live.Select(connection => connection.Id).ToHashSet();
        // Una sola lectura de las cuentas de cobro para la validación y la lista que se ofrece.
        var billingAccounts = await billingAccountOptions.ListAsync(agent.TenantId, cancellationToken);
        var issues = await ValidateAsync(agent, publishing: false, liveIds, billingAccounts, cancellationToken);
        var selectable = await ListSelectableAsync(agent.TenantId, live, cancellationToken);
        var assignable = await members.ListAsync(agent.TenantId, cancellationToken);
        var quoting = await advisors.ListAsync(agent.TenantId, cancellationToken);
        var published = agent.PublishedVersion;
        return new AgentEditorDto(
            agent.Id,
            agent.Name,
            agent.Status.ToString(),
            agent.Version,
            new AgentDraftDto(agent.DraftConfig, agent.DraftUpdatedAt, agent.DraftUpdatedBy),
            published is null ? null : new AgentPublishedDto(published.Number, published.PublishedAt, published.Provider, published.Model),
            agent.HasDraftChanges,
            AvailabilityOf(agent.DraftConfig.Model) != AgentModelAvailability.Available,
            [.. issues.Select(ToDto)],
            LiveConnectionIds(agent, liveIds),
            selectable,
            [.. assignable.Select(member => new AiMemberDto(member.Id, member.Name))],
            ModelCatalog.ToDto(catalog, await credentials.DescribeAsync(agent.TenantId, cancellationToken)),
            [.. quoting.Select(member => new AiMemberDto(member.Id, member.Name))],
            billingAccounts,
            await SchedulingReadinessAsync(agent.TenantId, cancellationToken),
            agent.SourceTemplateId,
            agent.SourceTemplateId is { } sourceId ? templates.Find(sourceId)?.Name : null);
    }

    /// <summary>§9.3: siempre se lee, con la herramienta prendida o no, para que el editor sepa si ofrecer el interruptor.</summary>
    private async Task<AgentSchedulingReadinessDto> SchedulingReadinessAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var readiness = await scheduling.GetReadinessAsync(tenantId, cancellationToken);
        return new AgentSchedulingReadinessDto(readiness.ModuleEnabled, readiness.BookableServices, readiness.Ready);
    }

    /// <param name="usedByChatbots">Spec 2026-11-17 §7: los chatbots publicados que usan cada agente (<see cref="IAiChatbotUsage"/>).</param>
    public async Task<AgentListResponse> BuildListAsync(
        Guid tenantId,
        IReadOnlyList<Agent> agents,
        AgentListActivity activity,
        IReadOnlyDictionary<Guid, IReadOnlyList<AiChatbotRef>> usedByChatbots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(usedByChatbots);
        var byId = (await connections.ListAsync(tenantId, cancellationToken)).ToDictionary(connection => connection.Id);
        return new AgentListResponse(
        [
            .. agents.Select(agent =>
            {
                var published = agent.PublishedVersion;
                var draft = agent.DraftConfig.Model;
                return new AgentSummaryDto(
                    agent.Id,
                    agent.Name,
                    agent.Status.ToString(),
                    published is null ? draft : new AgentModelRef(published.Provider, published.Model),
                    AvailabilityOf(draft) != AgentModelAvailability.Available,
                    [
                        .. agent.Connections
                            .Where(connection => byId.ContainsKey(connection.ConnectionId))
                            .Select(connection => byId[connection.ConnectionId])
                            .Select(connection => new AgentConnectionSummaryDto(connection.Id, connection.Name, connection.Channel)),
                    ],
                    published?.PublishedAt,
                    agent.HasDraftChanges,
                    agent.Version,
                    activity.UsageByAgent.GetValueOrDefault(agent.Id) ?? [],
                    activity.TodayByAgent.GetValueOrDefault(agent.Id) ?? AgentTodayDto.None,
                    usedByChatbots.GetValueOrDefault(agent.Id) ?? []);
            }),
        ],
        [
            .. activity.UsageByAgent.Values
                .SelectMany(usage => usage)
                .GroupBy(usage => usage.Provider, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new AgentUsageDto(
                    group.Key,
                    group.Sum(usage => usage.Calls),
                    group.Sum(usage => usage.InputTokens),
                    group.Sum(usage => usage.OutputTokens),
                    SumKnown(group.Select(usage => usage.CacheCreationInputTokens)),
                    SumKnown(group.Select(usage => usage.CacheReadInputTokens)))),
        ]);
    }

    /// <summary>C-4: <c>null</c> sólo si ningún agente lo reporta; si alguno lo reporta, la suma de los que sí.</summary>
    private static long? SumKnown(IEnumerable<long?> values)
    {
        long? total = null;
        foreach (var value in values)
        {
            if (value is { } known)
            {
                total = (total ?? 0) + known;
            }
        }

        return total;
    }

    public async Task<IReadOnlyList<AiConnectionDto>> ListSelectableAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await ListSelectableAsync(tenantId, await connections.ListAsync(tenantId, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<AiConnectionDto>> ListSelectableAsync(
        Guid tenantId, IReadOnlyList<AiConnectionRef> tenantConnections, CancellationToken cancellationToken)
    {
        var occupants = (await slots.ListAsync(tenantId, cancellationToken)).ToDictionary(occupant => occupant.ConnectionId);
        return
        [
            .. tenantConnections.Select(connection => new AiConnectionDto(
                connection.Id,
                connection.Name,
                connection.Channel,
                connection.Handle,
                connection.Status,
                occupants.TryGetValue(connection.Id, out var occupant) ? new AiServedByDto(occupant.Kind, occupant.Id, occupant.Name) : null,
                SupportsBot(connection.Channel))),
        ];
    }

    /// <summary>Cada conexión tiene que ser del tenant (404 si no, sin decir de quién es) y de un canal con salida de bot (422).</summary>
    public async Task EnsureConnectionsAsync(Guid tenantId, IReadOnlyList<Guid> connectionIds, CancellationToken cancellationToken)
    {
        var byId = (await connections.ListAsync(tenantId, cancellationToken)).ToDictionary(connection => connection.Id);
        foreach (var id in connectionIds)
        {
            if (!byId.TryGetValue(id, out var connection))
            {
                throw AiNotFound.Connection(id);
            }

            if (!SupportsBot(connection.Channel))
            {
                throw new AiDomainException(
                    AiErrorCodes.AgentChannelNotSupported, "Un agente IA sólo puede atender conexiones de WhatsApp o Instagram.");
            }
        }
    }

    public static AgentIssueDto ToDto(AgentIssue issue) => new(issue.Severity.ToString(), issue.Code, issue.Path, issue.Message);
}
