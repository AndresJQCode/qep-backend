using Microsoft.Extensions.Logging.Abstractions;
using Modules.Automations.Application;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Flow;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-11-17 (agente IA en chatbots) §7: el editor trae los agentes del tenant y si tiene el módulo <c>ai</c>; la lista, el
/// consumo de IA del mes por chatbot.</summary>
public sealed class ChatbotEditorBuilderTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string EmptyFlow = """{ "schema": 1, "start": { "firstNodeId": null }, "variables": [], "nodes": [] }""";

    private static Chatbot NewChatbot() =>
        Chatbot.Create(TenantId, "Bienvenida", string.Empty, ChatbotTrigger.NewConversation, [], EmptyFlow, "{}", Guid.CreateVersion7(), DateTimeOffset.UtcNow);

    [Fact]
    public async Task TheEditorListsTheTenantAgentsOnlyWithAi()
    {
        var agent = new AutomationAgentRef(Guid.CreateVersion7(), "Ventas", "Paused", "anthropic", "claude-sonnet-5-5");
        var directory = new Directory([agent]);

        var withAi = await Builder(directory, aiEnabled: true).BuildAsync(NewChatbot(), Ct);
        var withoutAi = await Builder(directory, aiEnabled: false).BuildAsync(NewChatbot(), Ct);

        Assert.True(withAi.AiEnabled);
        Assert.Equal([new ChatbotEditorAgentDto(agent.Id, "Ventas", "Paused", "anthropic", "claude-sonnet-5-5")], withAi.Agents);
        Assert.False(withoutAi.AiEnabled);
        Assert.Empty(withoutAi.Agents);
    }

    [Fact]
    public async Task TheListSumsTheAiUsageOfTheMonthPerChatbot()
    {
        var used = Guid.CreateVersion7();
        var unused = Guid.CreateVersion7();
        var directory = new Directory([]) { Usage = { [used] = (1200, 340) } };
        var rows = new[] { Row(used), Row(unused) };

        var list = await Builder(directory, aiEnabled: true).BuildListAsync(TenantId, rows, Ct);

        Assert.Equal(new ChatbotAiUsageDto(1200, 340), list.Items.Single(item => item.Id == used).AiUsageThisMonth);
        Assert.Null(list.Items.Single(item => item.Id == unused).AiUsageThisMonth);
        // §7: el mes del reloj del tenant (Bogotá, UTC-5): del 1 a las 05:00 UTC al 1 del mes siguiente.
        Assert.Equal((new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 1, 5, 0, 0, TimeSpan.Zero)), directory.Window);
    }

    // Review Focus 2: un flujo pegado de otro tenant trae un fileId que el store no resuelve para este tenant.
    [Fact]
    public async Task AForeignFileIsMissingAndNotListed()
    {
        var foreign = Guid.CreateVersion7();
        var store = new Media();

        var editor = await Builder(new Directory([]), aiEnabled: false, store).BuildAsync(WithMedia(foreign), Ct);

        Assert.Empty(editor.MediaFiles);
        Assert.Contains(editor.Issues, issue => issue.Code == "message.media_missing" && issue.NodeId == "m1");
        Assert.Equal([foreign], Assert.Single(store.Asked));
    }

    [Fact]
    public async Task AResolvedFileIsListedAndValid()
    {
        var fileId = Guid.CreateVersion7();
        var file = new ChatbotMediaFile(fileId, "image", "catalogo.jpg", "image/jpeg", 2048, new Uri($"https://public.test/{fileId}/catalogo.jpg"));
        var store = new Media { Files = { file } };

        var editor = await Builder(new Directory([]), aiEnabled: false, store).BuildAsync(WithMedia(fileId), Ct);

        Assert.Equal([ChatbotMediaFileDto.From(file)], editor.MediaFiles);
        Assert.DoesNotContain(editor.Issues, issue => issue.Code == "message.media_missing");

        // Guardar y publicar validan por ValidateAsync: el mismo archivo resuelve igual.
        var chatbot = WithMedia(fileId);
        var issues = await Builder(new Directory([]), aiEnabled: false, store).ValidateAsync(chatbot, FlowJson.Parse(chatbot.DraftFlowJson), Ct);
        Assert.DoesNotContain(issues, issue => issue.Code == "message.media_missing");
    }

    [Fact]
    public async Task AFlowWithoutMediaNeverAsksTheStore()
    {
        var store = new Media();

        var editor = await Builder(new Directory([]), aiEnabled: false, store).BuildAsync(NewChatbot(), Ct);
        await Builder(new Directory([]), aiEnabled: false, store).ValidateStoredAsync(NewChatbot(), Ct);

        Assert.Empty(editor.MediaFiles);
        Assert.Empty(store.Asked);
    }

    // ---- Spec 2026-11-19 (chatbot CRM action): the tenant's tags and open stages feed the editor and the validator. ----

    private static readonly Guid KnownTag = Guid.CreateVersion7();
    private static readonly Guid OpenStage = Guid.CreateVersion7();

    private static CrmCatalog FullCatalog() =>
        new(true, true, [new CrmTagRef(KnownTag, "Interesado")], [new CrmStageRef(OpenStage, "Nuevo"), new CrmStageRef(Guid.CreateVersion7(), "Calificado")]);

    private static Chatbot WithCrm(string operation, Guid? tagId, Guid? stageId)
    {
        var tag = tagId is null ? "null" : $"\"{tagId}\"";
        var stage = stageId is null ? "null" : $"\"{stageId}\"";
        return Chatbot.Create(
            TenantId, "CRM", string.Empty, ChatbotTrigger.NewConversation, [],
            $$"""
            { "schema": 1, "start": { "firstNodeId": "crm" }, "variables": [], "nodes": [
              { "id": "crm", "kind": "crmAction", "title": "Acción", "operation": "{{operation}}",
                "tagId": {{tag}}, "stageId": {{stage}},
                "saveTo": "crm", "outputs": { "ok": null, "skipped": null, "error": null } } ] }
            """,
            "{}", Guid.CreateVersion7(), DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task EditorBuilderFeedsTheValidatorWithTheCatalog()
    {
        var crm = new CrmDirectory(FullCatalog());
        var builder = Builder(new Directory([]), aiEnabled: false, crm: crm);

        var foreign = await builder.BuildAsync(WithCrm("addTag", Guid.CreateVersion7(), null), Ct);
        var known = await builder.BuildAsync(WithCrm("addTag", KnownTag, null), Ct);
        var stage = await builder.BuildAsync(WithCrm("moveDeal", null, OpenStage), Ct);

        Assert.Contains(foreign.Issues, issue => issue.Code == FlowIssueCodes.CrmTagMissing && issue.NodeId == "crm");
        Assert.DoesNotContain(known.Issues, issue => issue.Code.StartsWith("crm.", StringComparison.Ordinal));
        Assert.DoesNotContain(stage.Issues, issue => issue.Code.StartsWith("crm.", StringComparison.Ordinal));

        // Saving and publishing validate through ValidateAsync / ValidateStoredAsync: the same catalog.
        var stored = await builder.ValidateStoredAsync(WithCrm("removeTag", Guid.CreateVersion7(), null), Ct);
        Assert.Contains(stored, issue => issue.Code == FlowIssueCodes.CrmTagMissing);
    }

    [Fact]
    public async Task TheEditorCarriesTheCatalog()
    {
        var catalog = FullCatalog();

        var editor = await Builder(new Directory([]), aiEnabled: false, crm: new CrmDirectory(catalog)).BuildAsync(NewChatbot(), Ct);

        Assert.Equal(
            new ChatbotEditorCrmDto(
                [new ChatbotEditorCrmRefDto(KnownTag, "Interesado")],
                [.. catalog.OpenStages.Select(item => new ChatbotEditorCrmRefDto(item.Id, item.Name))],
                true, true),
            editor.Crm,
            CrmComparer.Instance);
    }

    [Fact]
    public async Task ModuleOffGivesEmptyListsAndFlagsFalse()
    {
        var crm = new CrmDirectory(new CrmCatalog(false, false, [], []));

        var editor = await Builder(new Directory([]), aiEnabled: false, crm: crm).BuildAsync(WithCrm("addTag", KnownTag, null), Ct);

        Assert.False(editor.Crm.CustomersEnabled);
        Assert.False(editor.Crm.PipelineEnabled);
        Assert.Empty(editor.Crm.Tags);
        Assert.Empty(editor.Crm.Stages);
        Assert.Contains(editor.Issues, issue => issue.Code == FlowIssueCodes.CrmModuleOff);
    }

    /// <summary>Saving and publishing a flow without the block do not pay the Customers and Pipeline reads.</summary>
    [Fact]
    public async Task ValidatingAFlowWithoutTheBlockNeverReadsTheCatalog()
    {
        var crm = new CrmDirectory(FullCatalog());

        await Builder(new Directory([]), aiEnabled: false, crm: crm).ValidateStoredAsync(NewChatbot(), Ct);

        Assert.Equal(0, crm.Reads);
    }

    /// <summary>A broken CRM module never fails the editor GET: the catalog degrades to empty lists, as with both modules off.</summary>
    [Fact]
    public async Task AThrowingDirectoryDegradesToEmptyLists()
    {
        var crm = new CrmDirectory(FullCatalog()) { Failure = new InvalidOperationException("Customers is broken.") };

        var editor = await Builder(new Directory([]), aiEnabled: false, crm: crm).BuildAsync(WithCrm("addTag", KnownTag, null), Ct);

        Assert.Empty(editor.Crm.Tags);
        Assert.Empty(editor.Crm.Stages);
        Assert.False(editor.Crm.CustomersEnabled);
        Assert.False(editor.Crm.PipelineEnabled);
    }

    /// <summary>«Not yet» is not «broken»: a cancellation or a conflict still goes up.</summary>
    [Fact]
    public async Task ATransientDirectoryFailureStillPropagates()
    {
        var crm = new CrmDirectory(FullCatalog()) { Failure = new OperationCanceledException() };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Builder(new Directory([]), aiEnabled: false, crm: crm).BuildAsync(NewChatbot(), Ct));
    }

    private sealed class CrmDirectory(CrmCatalog catalog) : IAutomationsCrmDirectory
    {
        public int Reads { get; private set; }

        public Exception? Failure { get; init; }

        public Task<CrmCatalog> ReadAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            Reads++;
            return Failure is null ? Task.FromResult(catalog) : Task.FromException<CrmCatalog>(Failure);
        }
    }

    /// <summary>Records with lists compare the lists by reference; this compares them by content.</summary>
    private sealed class CrmComparer : IEqualityComparer<ChatbotEditorCrmDto>
    {
        public static readonly CrmComparer Instance = new();

        public bool Equals(ChatbotEditorCrmDto? x, ChatbotEditorCrmDto? y) =>
            x is not null && y is not null
            && x.Tags.SequenceEqual(y.Tags) && x.Stages.SequenceEqual(y.Stages)
            && x.CustomersEnabled == y.CustomersEnabled && x.PipelineEnabled == y.PipelineEnabled;

        public int GetHashCode(ChatbotEditorCrmDto obj) => 0;
    }

    private static Chatbot WithMedia(Guid fileId) =>
        Chatbot.Create(
            TenantId, "Catálogo", string.Empty, ChatbotTrigger.NewConversation, [],
            $$"""
            { "schema": 1, "start": { "firstNodeId": "m1" }, "variables": [], "nodes": [
              { "id": "m1", "kind": "message", "title": "Catálogo", "text": "", "urlButton": null,
                "media": { "kind": "image", "fileId": "{{fileId}}" }, "outputs": { "next": null } } ] }
            """,
            "{}", Guid.CreateVersion7(), DateTimeOffset.UtcNow);

    private static ChatbotSummaryRow Row(Guid id) =>
        new(id, "Bot", string.Empty, ChatbotStatus.Draft, ChatbotTrigger.NewConversation, [], null, null, DateTimeOffset.UtcNow, 1);

    private static ChatbotEditorBuilder Builder(Directory directory, bool aiEnabled, Media? media = null, CrmDirectory? crm = null) =>
        new(new NoConnections(), new NoExternalApis(), new NoMembers(), new NoHours(), new NoRuns(), new BogotaClock(), new NoSlots(), directory,
            new Modules(aiEnabled), media ?? new Media(), crm ?? new CrmDirectory(new CrmCatalog(false, false, [], [])),
            NullLogger<ChatbotEditorBuilder>.Instance);

    private sealed class Media : IChatbotMediaStore
    {
        public List<ChatbotMediaFile> Files { get; } = [];

        public List<IReadOnlyCollection<Guid>> Asked { get; } = [];

        public Task<ChatbotMediaSession> OpenUploadAsync(
            Guid tenantId, string fileName, string contentType, long sizeBytes, long maxBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ChatbotMediaCompletion> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ChatbotMediaFile>> ResolveAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
        {
            Asked.Add([.. fileIds]);
            return Task.FromResult<IReadOnlyList<ChatbotMediaFile>>([.. Files.Where(file => fileIds.Contains(file.FileId))]);
        }
    }

    private sealed class Directory(IReadOnlyList<AutomationAgentRef> agents) : IAutomationsAgentDirectory
    {
        public Dictionary<Guid, (long Input, long Output)> Usage { get; } = [];

        public (DateTimeOffset From, DateTimeOffset To)? Window { get; private set; }

        public Task<IReadOnlyList<AutomationAgentRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(agents);

        public Task<IReadOnlyDictionary<Guid, (long InputTokens, long OutputTokens)>> UsageByChatbotAsync(
            Guid tenantId, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken)
        {
            Window = (from, until);
            return Task.FromResult<IReadOnlyDictionary<Guid, (long, long)>>(Usage.ToDictionary(pair => pair.Key, pair => pair.Value));
        }
    }

    private sealed class Modules(bool aiEnabled) : ITenantModules
    {
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantModuleSet?>(TenantModuleSet.FromStored(
                aiEnabled ? [TenantModuleKeys.Messaging, TenantModuleKeys.Automations, TenantModuleKeys.Ai] : [TenantModuleKeys.Messaging, TenantModuleKeys.Automations]));
    }

    private sealed class BogotaClock : ITenantClock
    {
        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantCalendar(
                new DateTimeOffset(2026, 11, 17, 15, 0, 0, TimeSpan.Zero), TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")));
    }

    private sealed class NoConnections : IAutomationConnections
    {
        public Task<IReadOnlyList<AutomationConnectionRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutomationConnectionRef>>([]);
    }

    private sealed class NoExternalApis : IAutomationExternalApis
    {
        public Task<IReadOnlyList<ExternalApiRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalApiRef>>([]);

        public Task<ExternalApiResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoMembers : IAutomationMembers
    {
        public Task<IReadOnlyList<AutomationMemberRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutomationMemberRef>>([]);
    }

    private sealed class NoHours : IAutomationBusinessHours
    {
        public Task<IReadOnlyDictionary<Guid, string?>> ScheduleNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string?>>(new Dictionary<Guid, string?>());

        public Task<bool> IsOpenAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class NoRuns : IConversationRunQueries
    {
        public Task<IReadOnlyDictionary<Guid, (int Handled, int HandedOff)>> TodayAsync(
            Guid tenantId, IReadOnlyCollection<Guid> chatbotIds, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, (int, int)>>(new Dictionary<Guid, (int, int)>());

        public Task<IReadOnlyList<ConversationRun>> ListLiveAsync(Guid tenantId, Guid chatbotId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoSlots : IChatbotConnectionSlots
    {
        public Task<IReadOnlyList<ConnectionOccupant>> SyncChatbotsAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConnectionOccupant>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConnectionOccupant>>([]);
    }
}
