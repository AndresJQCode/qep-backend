using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Plan 2026-11-12 (correcciones de la revisión), Tarea 2: Integrations borra una conexión sin avisar, y la referencia de
/// <c>ai.agent_connections</c> es blanda. Una conexión que ya no existe no cuenta para publicar ni vuelve al editor.</summary>
public sealed class AgentEditorBuilderTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid By = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ADeletedConnectionDoesNotCountForPublishing()
    {
        var deletedId = Guid.CreateVersion7();
        var agent = NewAgent();
        agent.SetConnections([deletedId], Now);

        var issues = await Builder(new FakeConnections()).ValidateAsync(agent, publishing: true, Ct);

        Assert.Contains(issues, issue => issue.Code == "connections_missing");
    }

    [Fact]
    public async Task TheEditorNamesTheTemplateTheAgentCameFrom()
    {
        var templates = new FakeTemplates(new SystemAgentTemplate("asistente-de-citas", "Asistente de citas", "Reserva citas.", [], "{}"));

        var editor = await Builder(new FakeConnections(), templates: templates).BuildAsync(AgentFromTemplate(), Ct);

        Assert.Equal(("asistente-de-citas", "Asistente de citas"), (editor.SourceTemplateId, editor.SourceTemplateName));
    }

    [Fact]
    public async Task ATemplateThatNoLongerExistsKeepsTheIdWithoutAName()
    {
        var editor = await Builder(new FakeConnections()).BuildAsync(AgentFromTemplate(), Ct);

        Assert.Equal(("asistente-de-citas", (string?)null), (editor.SourceTemplateId, editor.SourceTemplateName));
    }

    [Fact]
    public async Task AnAgentWithoutATemplateHasNoSource()
    {
        var editor = await Builder(new FakeConnections()).BuildAsync(NewAgent(), Ct);

        Assert.Equal(((string?)null, (string?)null), (editor.SourceTemplateId, editor.SourceTemplateName));
    }

    [Fact]
    public async Task TheEditorOnlyReturnsLiveConnections()
    {
        var liveId = Guid.CreateVersion7();
        var deletedId = Guid.CreateVersion7();
        var agent = NewAgent();
        agent.SetConnections([liveId, deletedId], Now);

        var editor = await Builder(new FakeConnections(new AiConnectionRef(liveId, "Ventas", "WhatsApp", "+57 300", "Active")))
            .BuildAsync(agent, Ct);

        Assert.Equal([liveId], editor.ConnectionIds);
        Assert.DoesNotContain(editor.Issues, issue => issue.Code == "connections_missing");
    }

    // Plan 2026-11-12 (correcciones), Tarea 5: el autoguardado valida el borrador en cada pausa al escribir. Para contar las conexiones
    // vivas bastan los ids; la lectura completa resuelve cada conexión activa con su secreto.
    [Fact]
    public async Task ValidatingReadsOnlyConnectionIds()
    {
        var liveId = Guid.CreateVersion7();
        var agent = NewAgent();
        agent.SetConnections([liveId], Now);
        var connections = new FakeConnections(new AiConnectionRef(liveId, "Ventas", "WhatsApp", "+57 300", "Active"));

        var issues = await Builder(connections).ValidateAsync(agent, publishing: false, Ct);

        Assert.DoesNotContain(issues, issue => issue.Code == "connections_missing");
        Assert.Equal((0, 1), (connections.ListCalls, connections.ListIdsCalls));
    }

    // P-4: el editor lista quién puede cotizar (otra lista que la del traspaso) y avisa si la asesora elegida ya no puede.
    [Fact]
    public async Task TheEditorListsTheQuotationAdvisorsAndWarnsWhenTheChosenOneCanNoLongerQuote()
    {
        var advisor = Guid.CreateVersion7();
        var agent = Agent.Create(
            Tenant,
            "Asesor virtual",
            AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")) with
            {
                Tools = new AgentTools(Catalog: false, OrderStatus: false, QuotationDraft: true),
                QuotationDraft = new AgentQuotationDraft(advisor, new AgentBillingAccount(Guid.CreateVersion7(), "Banco", "123", "COP")),
            },
            By,
            Now);
        var advisors = new FakeAdvisors(canQuote: false, new AiMemberRef(Guid.CreateVersion7(), "Ana"));

        var editor = await Builder(new FakeConnections(), advisors).BuildAsync(agent, Ct);

        Assert.Equal(["Ana"], editor.QuotationAdvisors.Select(member => member.Name));
        Assert.Contains(editor.Issues, issue => issue is { Code: "quotation_advisor_cannot_quote", Severity: "Warning" });
        Assert.Equal([advisor], advisors.Asked);
    }

    // Plan 2026-11-17 A2: el selector de modelo del editor dice lo mismo que GET /ai/models, con la misma lectura de las conexiones.
    [Fact]
    public async Task TheEditorModelsCarryTheProviderIssue()
    {
        var credentials = new FakeAiProviderCredentials(
        [
            new AiProviderConnectionState(AiProviders.Anthropic, Connectable: true, Guid.CreateVersion7(), "Active"),
            new AiProviderConnectionState(AiProviders.OpenAi, Connectable: true, Guid.CreateVersion7(), "NeedsAttention", "permission_denied"),
        ]);

        var editor = await Builder(new FakeConnections(), credentials: credentials).BuildAsync(NewAgent(), Ct);

        Assert.Equal(
            [("anthropic", true, (string?)null), ("openai", false, "permission_denied")],
            editor.Models.Providers.Select(provider => (provider.Provider, provider.Configured, provider.Issue)));
        Assert.Equal(1, credentials.DescribeCalls);
    }

    // Plan 2026-11-17 A3 (F-2): el editor ofrece las cuentas elegibles (el adaptador aplica la regla de la tienda) y avisa si la guardada ya
    // no está entre ellas. La comparación es la de Quotations: banco sin mayúsculas, número exacto y moneda, recortados.
    [Fact]
    public async Task TheEditorOffersTheEligibleBillingAccounts()
    {
        var company = Guid.CreateVersion7();
        var eligible = new AiBillingAccountOption(company, "Verde S.A.S.", "Bancolombia", "CTA-1", "COP");
        var billing = new FakeBillingAccounts(eligible);

        var matching = await Builder(new FakeConnections(), billing: billing)
            .BuildAsync(QuotingAgent(new AgentBillingAccount(company, " bancolombia ", "CTA-1", "cop")), Ct);
        var gone = await Builder(new FakeConnections(), billing: billing)
            .BuildAsync(QuotingAgent(new AgentBillingAccount(company, "Bancolombia", "CTA-2", "COP")), Ct);

        Assert.Equal([eligible], matching.QuotationBillingAccounts);
        Assert.DoesNotContain(matching.Issues, issue => issue.Code == "quotation_billing_account_unavailable");
        Assert.Contains(gone.Issues, issue => issue is { Code: "quotation_billing_account_unavailable", Severity: "Warning" });
        Assert.Equal(2, billing.ListCalls);
    }

    // Spec 2026-11-17 §7 y D-B18: cada fila de la lista trae los chatbots publicados cuyo flujo usa el agente; uno que nadie usa trae [].
    [Fact]
    public async Task TheAgentListShowsTheChatbotsThatUseIt()
    {
        var used = NewAgent();
        var unused = Agent.Create(Tenant, "Otro", AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")), By, Now);
        var chatbot = new AiChatbotRef(Guid.CreateVersion7(), "Bienvenida");
        var usage = new Dictionary<Guid, IReadOnlyList<AiChatbotRef>> { [used.Id] = [chatbot] };

        var list = await Builder(new FakeConnections()).BuildListAsync(
            Tenant, [used, unused], new AgentListActivity(new Dictionary<Guid, IReadOnlyList<AgentUsageDto>>(), new Dictionary<Guid, AgentTodayDto>()),
            usage, Ct);

        Assert.Equal([chatbot], list.Items.Single(item => item.Id == used.Id).UsedByChatbots);
        Assert.Empty(list.Items.Single(item => item.Id == unused.Id).UsedByChatbots);
    }

    // Spec 2026-11-23 §9.3: el editor siempre trae la disponibilidad de la Agenda; el validador la lee sólo con la herramienta prendida.
    [Fact]
    public async Task TheEditorAlwaysCarriesTheSchedulingReadiness()
    {
        var editor = await Builder(new FakeConnections(), scheduling: new FakeScheduling(new AgentSchedulingReadiness(true, 2)))
            .BuildAsync(NewAgent(), Ct);

        Assert.Equal(new AgentSchedulingReadinessDto(true, 2, true), editor.Scheduling);
        Assert.DoesNotContain(editor.Issues, issue => issue.Code.StartsWith("scheduling_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidationReadsReadinessOnlyWithTheTool()
    {
        var scheduling = new FakeScheduling(new AgentSchedulingReadiness(false, 0));
        var off = NewAgent();
        var on = NewAgent();
        on.SaveDraft(on.DraftConfig with { Tools = new AgentTools(false, false, false, Scheduling: true) }, By, Now);

        await Builder(new FakeConnections(), scheduling: scheduling).ValidateAsync(off, publishing: true, Ct);
        Assert.Equal(0, scheduling.Calls);

        var issues = await Builder(new FakeConnections(), scheduling: scheduling).ValidateAsync(on, publishing: true, Ct);
        Assert.Equal(1, scheduling.Calls);
        Assert.Contains(issues, issue => issue.Code == "scheduling_module_disabled");
    }

    private static Agent QuotingAgent(AgentBillingAccount account) =>
        Agent.Create(
            Tenant,
            "Asesor virtual",
            AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")) with
            {
                Tools = new AgentTools(Catalog: false, OrderStatus: false, QuotationDraft: true),
                QuotationDraft = new AgentQuotationDraft(Guid.CreateVersion7(), account),
            },
            By,
            Now);

    private static Agent NewAgent() =>
        Agent.Create(Tenant, "Asesor virtual", AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")), By, Now);

    private static AgentEditorBuilder Builder(
        IAiConnections connections,
        FakeAdvisors? advisors = null,
        FakeAiProviderCredentials? credentials = null,
        FakeBillingAccounts? billing = null,
        FakeScheduling? scheduling = null,
        ISystemAgentTemplateCatalog? templates = null) =>
        new(
            connections,
            new FakeSlots(),
            new FakeMembers(),
            new FakeModelCatalog(),
            advisors ?? new FakeAdvisors(canQuote: true),
            credentials ?? new FakeAiProviderCredentials([]),
            billing ?? new FakeBillingAccounts(),
            scheduling ?? new FakeScheduling(new AgentSchedulingReadiness(true, 1)),
            templates ?? new FakeTemplates());

    private sealed class FakeTemplates(params SystemAgentTemplate[] templates) : ISystemAgentTemplateCatalog
    {
        public IReadOnlyList<SystemAgentTemplate> All => templates;

        public SystemAgentTemplate? Find(string id) => templates.FirstOrDefault(template => string.Equals(template.Id, id, StringComparison.Ordinal));
    }

    private static Agent AgentFromTemplate() =>
        Agent.Create(
            Tenant, "Citas", AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")), By, Now, sourceTemplateId: "asistente-de-citas");

    /// <summary>Sólo la lectura de disponibilidad importa acá; el resto del puerto no se toca.</summary>
    private sealed class FakeScheduling(AgentSchedulingReadiness readiness) : IAiScheduling
    {
        public int Calls { get; private set; }

        public Task<AgentSchedulingReadiness> GetReadinessAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(readiness);
        }

        public Task<AiSchedulingResult<AiBookableServices>> ListBookableServicesAsync(AiSchedulingCaller caller, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AiSchedulingResult<AiAvailability>> SearchAvailabilityAsync(AiSchedulingCaller caller, Guid serviceId, DateOnly fromDate, DateOnly toDate, Guid? resourceId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AiSchedulingResult<AiAppointmentWrite>> BookAsync(AiSchedulingCaller caller, Guid serviceId, DateTime localStartsAt, Guid? resourceId, string? note, string clientRequestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AiSchedulingResult<AiStartPreview>> PreviewBookingAsync(AiSchedulingCaller caller, Guid serviceId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AiSchedulingResult<AiReschedulePreview>> PreviewRescheduleAsync(AiSchedulingCaller caller, Guid appointmentId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AiSchedulingResult<AiAppointments>> ListMyAppointmentsAsync(AiSchedulingCaller caller, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AiSchedulingResult<AiAppointment>> FindMyAppointmentAsync(AiSchedulingCaller caller, Guid appointmentId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AiSchedulingResult<AiAppointmentWrite>> CancelAsync(AiSchedulingCaller caller, Guid appointmentId, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AiSchedulingResult<AiAppointmentWrite>> RescheduleAsync(AiSchedulingCaller caller, Guid appointmentId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeBillingAccounts(params AiBillingAccountOption[] options) : IAiQuotationBillingAccounts
    {
        public int ListCalls { get; private set; }

        public Task<IReadOnlyList<AiBillingAccountOption>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<AiBillingAccountOption>>(options);
        }
    }

    private sealed class FakeAdvisors(bool canQuote, params AiMemberRef[] members) : IAiQuotationAdvisors
    {
        public List<Guid> Asked { get; } = [];

        public Task<IReadOnlyList<AiMemberRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiMemberRef>>(members);

        public Task<bool> CanQuoteAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken)
        {
            Asked.Add(memberId);
            return Task.FromResult(canQuote);
        }
    }

    private sealed class FakeConnections(params AiConnectionRef[] connections) : IAiConnections
    {
        public int ListCalls { get; private set; }

        public int ListIdsCalls { get; private set; }

        public Task<IReadOnlyList<AiConnectionRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<AiConnectionRef>>(connections);
        }

        public Task<IReadOnlySet<Guid>> ListIdsAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            ListIdsCalls++;
            return Task.FromResult<IReadOnlySet<Guid>>(connections.Select(connection => connection.Id).ToHashSet());
        }
    }

    private sealed class FakeSlots : IAgentConnectionSlots
    {
        public Task<IReadOnlyList<AiConnectionOccupant>> SyncAgentsAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiConnectionOccupant>>([]);

        public Task<IReadOnlyList<AiConnectionOccupant>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiConnectionOccupant>>([]);
    }

    private sealed class FakeMembers : IAiMembers
    {
        public Task<IReadOnlyList<AiMemberRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiMemberRef>>([]);
    }

    private sealed class FakeModelCatalog : IAiModelCatalog
    {
        public AiProvider DefaultProvider => AiProviders.Anthropic;

        public IReadOnlyList<AiProviderModels> Providers { get; } =
        [
            new(AiProviders.Anthropic,
                [new("claude-opus-5-5", "Claude Opus 5.5", "El más capaz.", IsDefault: true, Available: true, Effort: "medium")]),
            new(AiProviders.OpenAi, [new("gpt-6-luna", "GPT-6 Luna", "", IsDefault: true, Available: false, Effort: null)]),
        ];
    }
}
