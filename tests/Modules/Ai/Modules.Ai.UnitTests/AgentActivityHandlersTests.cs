using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>
/// Spec 2026-10-29 §5 (<c>GET /conversations/{id}/agent-activity</c> y <c>GET /sessions</c>) y plan 2026-11-12 Tarea 9: cada respuesta del agente
/// muestra el proveedor y el modelo que de verdad respondió (C-4), sus fuentes ya resueltas a algo que se pueda leer y sus herramientas.
/// </summary>
public sealed class AgentActivityHandlersTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Conversation = Guid.Parse("01900000-0000-7000-8000-0000000000f1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EachSourceIsResolvedToAFaqAQuickReplyOrAToolCall()
    {
        var reader = new FakeReader { Conversation = [Session(["envios", "qr-1", "call_1", "qr-borrada"])] };

        var activity = await Handler(reader).HandleAsync(new GetConversationAgentActivityQuery(Tenant, Conversation), Ct);

        var response = Assert.Single(Assert.Single(activity.Sessions).Responses);
        Assert.Equal(
            [
                new AgentActivitySourceDto("faq", "envios", "¿Hacen envíos?"),
                new AgentActivitySourceDto("quickReply", "qr-1", "/envio"),
                new AgentActivitySourceDto("tool", "call_1", AgentToolNames.SearchCatalog),
                new AgentActivitySourceDto("quickReply", "qr-borrada", null),
            ],
            response.Sources);
    }

    // C-4: el proveedor es el de la versión de la sesión; los modelos servidos, los que reportó cada llamada (con el respaldo de C-9 pueden
    // ser dos).
    [Fact]
    public async Task EachResponseCarriesTheProviderAndTheServedModels()
    {
        var reader = new FakeReader { Conversation = [Session([])] };

        var response = Assert.Single(Assert.Single(
            (await Handler(reader).HandleAsync(new GetConversationAgentActivityQuery(Tenant, Conversation), Ct)).Sessions).Responses);

        // Por separado: dentro de una tupla la lista se compararía por referencia, no por contenido.
        Assert.Equal("anthropic", response.Provider);
        Assert.Equal(["claude-sonnet-5-5", "claude-opus-5-5"], response.ServedModels);
        Assert.Equal(new AgentUsageDto("anthropic", 2, 3000, 120, 800, null), response.Usage);
    }

    // Plan 2026-11-17 A4 (F-4): la transcripción del playground trae sus fuentes resueltas como la actividad real: la FAQ sale de la foto del
    // borrador de la prueba y la herramienta de las llamadas de la prueba. SourceIds se conserva para el frontend que ya lo lee.
    [Fact]
    public async Task ThePlaygroundTranscriptResolvesItsSources()
    {
        var at = new DateTimeOffset(2026, 11, 17, 14, 0, 0, TimeSpan.Zero);
        var draft = AgentConfig.Default(new AgentModelRef("anthropic", "claude-sonnet-5-5")) with
        {
            Instructions = "Atiende.",
            Faq = [new AgentFaq("envios", "¿Hacen envíos?", "Sí.")],
            UseQuickReplies = true,
        };
        var test = AgentTest.Open(Guid.CreateVersion7(), Tenant, Guid.CreateVersion7(), Guid.CreateVersion7(), null, draft, at);
        var job = Guid.CreateVersion7();
        test.AddCustomerMessage("¿Envían a Cali?", job, at);
        test.RecordReply(job, "Sí, enviamos.", ["envios", "qr-1", "call_1", "qr-borrada"], at.AddSeconds(3));
        var tests = new FakeTests(test)
        {
            Activity = new AgentTestActivity(
                [
                    new AgentTestToolCallDto(
                        "call_1", AgentToolNames.SearchCatalog, JsonDocument.Parse("""{"query":"jabón"}""").RootElement, null, "ok", null, 40, at),
                ],
                []),
        };

        var dto = await new GetAgentTestHandler(
                new FakeExecutionContext(Tenant, AiPermissions.AgentManage), AllModules(), tests, new FakeQuickReplies(), new FixedClock(at))
            .HandleAsync(new GetAgentTestQuery(Tenant, test.Id), Ct);

        var reply = Assert.Single(dto.Transcript, entry => entry.Role == AgentTestEntry.Agent);
        Assert.Equal(["envios", "qr-1", "call_1", "qr-borrada"], reply.SourceIds);
        Assert.Equal(
            [
                new AgentActivitySourceDto("faq", "envios", "¿Hacen envíos?"),
                new AgentActivitySourceDto("quickReply", "qr-1", "/envio"),
                new AgentActivitySourceDto("tool", "call_1", AgentToolNames.SearchCatalog),
                new AgentActivitySourceDto("quickReply", "qr-borrada", null),
            ],
            reply.Sources);
        Assert.Empty(Assert.Single(dto.Transcript, entry => entry.Role == AgentTestEntry.Customer).Sources);
    }

    [Fact]
    public async Task WithoutReadPermissionTheActivityIsDenied()
    {
        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            new GetConversationAgentActivityHandler(
                    new FakeExecutionContext(Tenant), AllModules(), new FakeReader(), new FakeQuickReplies(), new FakeChatbotNames(new Dictionary<Guid, string>()))
                .HandleAsync(new GetConversationAgentActivityQuery(Tenant, Conversation), Ct));

        Assert.Equal("authorization.denied", error.Code);
    }

    // Plan 2026-11-17 A5 (F-6): from y to son días del tenant, los dos inclusivos (precedente GetMessagingReportSummary): Bogotá es UTC-5, así
    // que el 1 de noviembre empieza a las 05:00 UTC y el 15 termina a las 05:00 UTC del 16.
    [Fact]
    public async Task TheHistoryFiltersByTenantDaysInclusive()
    {
        var reader = new FakeReader();
        var agent = Guid.CreateVersion7();

        await SessionsHandler(reader).HandleAsync(
            new ListAgentSessionsQuery(Tenant, agent, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 15), null), Ct);

        Assert.Equal(
            (agent, new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 16, 5, 0, 0, TimeSpan.Zero), (AgentSessionCursor?)null,
                ListAgentSessionsHandler.PageSize + 1),
            reader.SessionsAsked);
    }

    // 50 por página, de la más reciente a la más vieja, y el cursor sigue donde terminó la página: ninguna se repite ni se salta, aunque dos
    // empiecen en el mismo instante (desempata el id).
    [Fact]
    public async Task TheHistoryPagesWithACursorWithoutRepeating()
    {
        var at = new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);
        var reader = new FakeReader
        {
            Stored = [.. Enumerable.Range(0, 75).Select(index => SessionRow(at.AddMinutes(-(index / 2))))],
        };

        var first = await SessionsHandler(reader).HandleAsync(new ListAgentSessionsQuery(Tenant, null, null, null, null), Ct);
        var second = await SessionsHandler(reader).HandleAsync(new ListAgentSessionsQuery(Tenant, null, null, null, first.NextCursor), Ct);

        Assert.Equal(ListAgentSessionsHandler.PageSize, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(25, second.Items.Count);
        Assert.Null(second.NextCursor);
        var expected = reader.Stored.OrderByDescending(session => session.StartedAt).ThenByDescending(session => session.Id).Select(session => session.Id);
        Assert.Equal(expected, first.Items.Concat(second.Items).Select(session => session.Id));
    }

    [Fact]
    public void ToBeforeFromIsRejected()
    {
        var validator = new ListAgentSessionsValidator();

        var backwards = validator.Validate(new ListAgentSessionsQuery(Tenant, null, new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 1), null));
        var sameDay = validator.Validate(new ListAgentSessionsQuery(Tenant, null, new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 15), null));
        var forged = validator.Validate(new ListAgentSessionsQuery(Tenant, null, null, null, "no-es-un-cursor"));

        Assert.Equal(["to"], backwards.Errors.Select(error => error.PropertyName));
        Assert.True(sameDay.IsValid);
        Assert.Equal(["cursor"], forged.Errors.Select(error => error.PropertyName));
    }

    // Spec 2026-11-17 §7: cada sesión dice de dónde salió; la de un bloque trae su chatbot (con el nombre de ahora) y la salida por la que
    // volvió al flujo. Un chatbot borrado conserva el id y llega sin nombre.
    [Fact]
    public async Task TheSessionHistoryShowsTheChatbotOfABlockSession()
    {
        var at = new DateTimeOffset(2026, 11, 17, 14, 0, 0, TimeSpan.Zero);
        var (chatbot, gone) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var reader = new FakeReader
        {
            Stored =
            [
                SessionRow(at) with { Origin = "ChatbotBlock", Chatbot = new AgentSessionChatbotDto(chatbot, null), Status = "Returned", EndReason = "done", Output = "done" },
                SessionRow(at.AddMinutes(-1)) with { Origin = "ChatbotBlock", Chatbot = new AgentSessionChatbotDto(gone, null), Status = "Live" },
                SessionRow(at.AddMinutes(-2)),
            ],
        };
        var names = new FakeChatbotNames(new Dictionary<Guid, string> { [chatbot] = "Bienvenida" });

        var page = await SessionsHandler(reader, names).HandleAsync(new ListAgentSessionsQuery(Tenant, null, null, null, null), Ct);

        Assert.Equal(
            [("ChatbotBlock", (Guid?)chatbot, "Bienvenida", "done"), ("ChatbotBlock", gone, null, null), ("Connection", null, null, null)],
            page.Items.Select(item => (item.Origin, item.Chatbot?.Id, item.Chatbot?.Name, item.Output)));
        Assert.Equal([[chatbot, gone]], names.Asked);
    }

    // Spec 2026-11-17 §7: la actividad de la conversación dice de dónde salió cada sesión, con los mismos nombres y la misma semántica que el
    // historial (GET /sessions): origen, chatbot con su nombre de ahora (sin nombre si se borró) y la salida por la que volvió al flujo.
    [Fact]
    public async Task TheConversationActivityShowsTheOriginOfABlockSession()
    {
        var (chatbot, gone) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var reader = new FakeReader
        {
            Conversation =
            [
                Session([]),
                Session([]) with { Origin = "ChatbotBlock", ChatbotId = gone, Status = "Live" },
                Session([]) with { Origin = "ChatbotBlock", ChatbotId = chatbot, Status = "Returned", EndReason = "done", Output = "done" },
            ],
        };
        var names = new FakeChatbotNames(new Dictionary<Guid, string> { [chatbot] = "Bienvenida" });

        var activity = await Handler(reader, names).HandleAsync(new GetConversationAgentActivityQuery(Tenant, Conversation), Ct);

        Assert.Equal(
            [("Connection", null, null, null), ("ChatbotBlock", (Guid?)gone, null, null), ("ChatbotBlock", chatbot, "Bienvenida", "done")],
            activity.Sessions.Select(session => (session.Origin, session.Chatbot?.Id, session.Chatbot?.Name, session.Output)));
        Assert.Single(names.Asked);
        // Se ordenan los dos lados: dos Guid v7 del mismo milisegundo no salen en orden de creación (los bits bajos son aleatorios).
        Assert.Equal(new[] { gone, chatbot }.Order(), names.Asked[0].Order());
    }

    [Fact]
    public async Task WithoutBlockSessionsNoChatbotIsLookedUp()
    {
        var names = new FakeChatbotNames(new Dictionary<Guid, string>());

        await Handler(new FakeReader { Conversation = [Session([])] }, names).HandleAsync(new GetConversationAgentActivityQuery(Tenant, Conversation), Ct);

        Assert.Empty(names.Asked);
    }

    // Spec 2026-11-17 §7 y D-B17: la prueba en modo bloque ofrece finish_objective, lleva el objetivo en el contexto volátil y la transcripción
    // muestra el retorno con su salida, su motivo y el resumen, en vez de un traspaso.
    [Fact]
    public async Task ABlockTestOffersFinishObjectiveAndShowsTheReturn()
    {
        var at = new DateTimeOffset(2026, 11, 17, 14, 0, 0, TimeSpan.Zero);
        var draft = AgentConfig.Default(new AgentModelRef("anthropic", "claude-sonnet-5-5")) with { Instructions = "Atiende." };
        var test = AgentTest.Open(
            Guid.CreateVersion7(), Tenant, Guid.CreateVersion7(), Guid.CreateVersion7(), null, draft, at, new AgentTestBlock("Resuelve dudas de precios.", "Eligió «Precios»."));
        var job = ModelJob.ForTest(Guid.CreateVersion7(), Tenant, test.Id, 1, at);
        test.AddCustomerMessage("¿Cuánto cuesta?", job.Id, at);
        var model = new AgentSessionProcessorTests.ScriptedModel();
        model.Enqueue(new AiModelResponse(
            null, [new AiToolCall("final", AgentToolNames.FinishObjective, """{"summary":"Le di el precio."}""")], [], AiStopReason.ToolUse,
            new AiModelUsage(10, 5, null, null), "claude-sonnet-5-5", "req_1"));
        var credentials = new AgentSessionProcessorTests.FakeCredentials();
        credentials.Available.Add("anthropic");
        var limits = new AiRunLimits(16_000, TimeSpan.FromSeconds(120), 6, 8, 30, null);
        var processor = new AgentTestJobProcessor(
            new FakeTests(test), credentials, new AgentSessionProcessorTests.EmptyCatalog(), new FakeQuickReplies(),
            new FixedTenantClock(at), new AgentPromptBuilder(limits),
            new AgentTurnRunner(
                new AgentSessionProcessorTests.SingleResolver(model), [], new AgentSessionProcessorTests.NoLedger(), credentials, limits,
                new FixedClock(at), Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentTurnRunner>.Instance),
            new NoUnitOfWork(), new FixedClock(at), Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentTestJobProcessor>.Instance);

        await processor.ProcessAsync(job, Ct);

        var request = Assert.Single(model.Requests);
        Assert.Contains(request.Tools, tool => tool.Name == AgentToolNames.FinishObjective);
        Assert.Contains("Resuelve dudas de precios.", request.VolatileContext, StringComparison.Ordinal);
        var dto = await new GetAgentTestHandler(
                new FakeExecutionContext(Tenant, AiPermissions.AgentManage), AllModules(), new FakeTests(test), new FakeQuickReplies(), new FixedClock(at))
            .HandleAsync(new GetAgentTestQuery(Tenant, test.Id), Ct);
        Assert.Equal(new AgentTestReturnedDto("done", "objective_completed", "Le di el precio."), dto.Returned);
        Assert.Equal(new AgentTestBlock("Resuelve dudas de precios.", "Eligió «Precios»."), dto.Block);
        var entry = dto.Transcript[^1];
        Assert.Equal((AgentTestEntry.Returned, "done", "objective_completed", "Le di el precio."), (entry.Role, entry.Output, entry.Reason, entry.Text));
        Assert.False(dto.HandedOff);
        Assert.False(dto.Pending);
    }

    // §7: el objetivo es obligatorio y los dos textos miden a lo sumo 2000, como en el bloque del flujo.
    [Fact]
    public void ABlockTestNeedsAnObjectiveWithinItsLimits()
    {
        var validator = new CreateAgentTestValidator();

        var blank = validator.Validate(new CreateAgentTestCommand(Tenant, Guid.CreateVersion7(), null, new AgentTestBlock("  ", null)));
        var tooLong = validator.Validate(new CreateAgentTestCommand(
            Tenant, Guid.CreateVersion7(), null, new AgentTestBlock(new string('o', 2001), new string('c', 2001))));
        var plain = validator.Validate(new CreateAgentTestCommand(Tenant, Guid.CreateVersion7(), null));

        Assert.Equal(["Block.Objective"], blank.Errors.Select(error => error.PropertyName));
        Assert.Equal(["Block.Context", "Block.Objective"], tooLong.Errors.Select(error => error.PropertyName).Order(StringComparer.Ordinal));
        Assert.True(plain.IsValid);
    }

    private static ListAgentSessionsHandler SessionsHandler(FakeReader reader, FakeChatbotNames? names = null) =>
        new(
            new FakeExecutionContext(Tenant, AiPermissions.AgentRead),
            AllModules(),
            reader,
            new ListAgentSessionsValidator(),
            new FixedTenantClock(new DateTimeOffset(2026, 11, 17, 15, 0, 0, TimeSpan.Zero)),
            names ?? new FakeChatbotNames(new Dictionary<Guid, string>()));

    private sealed class FakeChatbotNames(IReadOnlyDictionary<Guid, string> names) : IAiChatbotNames
    {
        public List<Guid[]> Asked { get; } = [];

        public Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(Guid tenantId, IReadOnlyCollection<Guid> chatbotIds, CancellationToken cancellationToken)
        {
            Asked.Add([.. chatbotIds]);
            return Task.FromResult(names);
        }
    }

    private sealed class NoUnitOfWork : IAiUnitOfWork
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static AgentSessionDto SessionRow(DateTimeOffset startedAt) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "Asesor virtual", Guid.CreateVersion7(), 1, "anthropic", "claude-sonnet-5-5", "Ended",
            "resolved", null, startedAt, startedAt.AddMinutes(5), 1, []);

    private static GetConversationAgentActivityHandler Handler(FakeReader reader, FakeChatbotNames? names = null) =>
        new(
            new FakeExecutionContext(Tenant, AiPermissions.AgentRead), AllModules(), reader, new FakeQuickReplies(),
            names ?? new FakeChatbotNames(new Dictionary<Guid, string>()));

    private static FakeTenantModules AllModules() => new(TenantModuleSet.FromStored(TenantModuleKeys.All));

    private static AgentConversationSession Session(IReadOnlyList<string> sources)
    {
        var config = AgentConfig.Default(new AgentModelRef("anthropic", "claude-sonnet-5-5")) with
        {
            Instructions = "Atiende.",
            Faq = [new AgentFaq("envios", "¿Hacen envíos?", "Sí.")],
            UseQuickReplies = true,
        };
        var at = new DateTimeOffset(2026, 11, 15, 14, 0, 0, TimeSpan.Zero);
        return new AgentConversationSession(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "Asesor virtual", 1, "anthropic", "claude-sonnet-5-5", config.ToJson(), "Live", null, null, at,
            null,
            [
                new AgentConversationJob(
                    Guid.CreateVersion7(), "Done", "replied", null, at, at.AddSeconds(5), [Guid.CreateVersion7()], false, sources,
                    [
                        new AgentTestToolCallDto(
                            "call_1", AgentToolNames.SearchCatalog, JsonDocument.Parse("""{"query":"jabón","limit":3}""").RootElement, null, "ok", null, 40, at),
                    ],
                    ["claude-sonnet-5-5", "claude-opus-5-5"],
                    new AgentUsageDto("anthropic", 2, 3000, 120, 800, null)),
            ]);
    }

    private sealed class FakeReader : IAgentActivityReader
    {
        public IReadOnlyList<AgentConversationSession> Conversation { get; init; } = [];

        public IReadOnlyList<AgentSessionDto> Stored { get; init; } = [];

        public (Guid? Agent, DateTimeOffset? From, DateTimeOffset? To, AgentSessionCursor? After, int Take) SessionsAsked { get; private set; }

        public Task<AgentListActivity> SummarizeAsync(Guid tenantId, AgentActivityWindow window, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        // El keyset del lector real, en memoria: (started_at, id) < (@at, @id), de la más reciente a la más vieja.
        public Task<IReadOnlyList<AgentSessionDto>> ListSessionsAsync(
            Guid tenantId,
            Guid? agentId,
            DateTimeOffset? startedFrom,
            DateTimeOffset? startedBefore,
            AgentSessionCursor? after,
            int take,
            CancellationToken cancellationToken)
        {
            SessionsAsked = (agentId, startedFrom, startedBefore, after, take);
            return Task.FromResult<IReadOnlyList<AgentSessionDto>>(
            [
                .. Stored
                    .Where(session => after is null
                        || session.StartedAt < after.StartedAt
                        || (session.StartedAt == after.StartedAt && session.Id.CompareTo(after.Id) < 0))
                    .OrderByDescending(session => session.StartedAt)
                    .ThenByDescending(session => session.Id)
                    .Take(take),
            ]);
        }

        public Task<IReadOnlyList<AgentConversationSession>> LoadConversationAsync(
            Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => Task.FromResult(Conversation);
    }

    private sealed class FakeTests(AgentTest test) : IAgentTestRepository
    {
        public AgentTestActivity Activity { get; init; } = new([], []);

        public Task<AgentTest?> FindAsync(Guid tenantId, Guid testId, CancellationToken cancellationToken) =>
            Task.FromResult<AgentTest?>(tenantId == test.TenantId && testId == test.Id ? test : null);

        public Task<AgentTestActivity> LoadActivityAsync(Guid tenantId, Guid testId, CancellationToken cancellationToken) =>
            Task.FromResult(Activity);

        public void Add(AgentTest test) => throw new NotSupportedException();

        public void Remove(AgentTest test) => throw new NotSupportedException();
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>Bogotá sin horario de verano: UTC-5 fijo.</summary>
    private sealed class FixedTenantClock(DateTimeOffset now) : ITenantClock
    {
        private static readonly TimeZoneInfo Bogota =
            TimeZoneInfo.CreateCustomTimeZone("America/Bogota", TimeSpan.FromHours(-5), "Bogotá", "Bogotá");

        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantCalendar(now, Bogota));
    }

    private sealed class FakeQuickReplies : IAiQuickReplies
    {
        public Task<IReadOnlyList<AiQuickReply>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiQuickReply>>([new AiQuickReply("qr-1", "/envio", "Enviamos a todo el país.")]);
    }
}
