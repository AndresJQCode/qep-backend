using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Plan 2026-11-12, 5.5: el bucle común con un <see cref="IAiModelClient"/> falso. Nada de esto depende del proveedor.</summary>
public sealed class AgentTurnRunnerTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid ConnectionId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");

    private readonly FakeModelClient _model = new();
    private readonly FakeResolver _resolver;
    private readonly FakeLedger _ledger = new();
    private readonly FakeCredentials _credentials = new();
    private readonly FakeCatalog _catalog = new();

    public AgentTurnRunnerTests() => _resolver = new FakeResolver(_model);

    [Fact]
    public async Task TheRunnerAsksTheResolverForTheProviderOfTheVersion()
    {
        _model.Enqueue(Reply("Hola."));

        await RunAsync(Context() with { Provider = AiProviders.OpenAi, Model = "gpt-6-luna" });

        Assert.Equal([AiProviders.OpenAi], _resolver.Asked);
        Assert.Equal("gpt-6-luna", _model.Requests.Single().Model);
    }

    [Fact]
    public async Task SeveralToolCallsInOneTurnGoBackAsOneRoundWithAllTheirResults()
    {
        _model.Enqueue(Tools(Call("call_1", AgentToolNames.SearchCatalog, new { query = "jabón", limit = 3 }),
            Call("call_2", AgentToolNames.SearchCatalog, new { query = "champú", limit = 2 })));
        _model.Enqueue(Reply("Tenemos jabón y champú.", "call_1", "call_2"));

        var outcome = await RunAsync(Context());

        var second = _model.Requests[1];
        var round = Assert.Single(second.PriorAssistantBlocks);
        Assert.Equal(["call_1", "call_2"], round.ToolResults.Select(result => result.CallId));
        Assert.All(round.ToolResults, result => Assert.False(result.IsError));
        Assert.Equal(["opaque-1"], round.AssistantBlocks.Select(block => block.Json));
        var replied = Assert.IsType<AgentReplied>(outcome);
        Assert.Equal(["call_1", "call_2"], replied.SourceIds);
        Assert.Equal(["call_1", "call_2"], _ledger.ToolCalls.Select(call => call.CallId));
    }

    [Fact]
    public async Task AToolThatFailsComesBackWithIsErrorAndIsRecorded()
    {
        _model.Enqueue(Tools(
            Call("call_1", AgentToolNames.SearchCatalog, new { query = "", limit = 3 }),
            Call("call_2", "drop_database", new { })));
        _model.Enqueue(Reply("No encontré eso."));

        await RunAsync(Context());

        var round = Assert.Single(_model.Requests[1].PriorAssistantBlocks);
        Assert.All(round.ToolResults, result => Assert.True(result.IsError));
        Assert.Equal(
            [AgentToolErrors.InvalidArguments, AgentToolErrors.UnknownTool],
            _ledger.ToolCalls.Select(call => call.ErrorCode));
        Assert.All(_ledger.ToolCalls, call => Assert.Equal(AiRunOutcomes.Error, call.Outcome));
    }

    [Fact]
    public async Task OnlyTheToolsTheAgentHasEnabledAreOffered()
    {
        _model.Enqueue(Reply("Hola."));

        await RunAsync(Context() with { Config = Config() with { Tools = new AgentTools(false, false, false) } });

        Assert.Equal(
            [AgentToolNames.HandoffToHuman, AgentToolNames.SendReply],
            _model.Requests.Single().Tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PassingTheModelCallLimitFailsTheAgent()
    {
        for (var index = 0; index < 3; index++)
        {
            _model.Enqueue(Tools(Call($"call_{index}", AgentToolNames.SearchCatalog, new { query = "x", limit = 1 })));
        }

        var outcome = await RunAsync(Context(), Limits() with { MaxModelCalls = 2 });

        var failed = Assert.IsType<AgentTurnFailed>(outcome);
        Assert.Equal((AgentFailureKind.AgentFailed, AgentFailureReason.ModelCallsExceeded), (failed.Kind, failed.Reason));
        Assert.Equal(2, _model.Requests.Count);
    }

    [Fact]
    public async Task PassingTheToolCallLimitFailsTheAgent()
    {
        _model.Enqueue(Tools(
            Call("call_1", AgentToolNames.SearchCatalog, new { query = "a", limit = 1 }),
            Call("call_2", AgentToolNames.SearchCatalog, new { query = "b", limit = 1 }),
            Call("call_3", AgentToolNames.SearchCatalog, new { query = "c", limit = 1 })));

        var outcome = await RunAsync(Context(), Limits() with { MaxToolCalls = 2 });

        Assert.Equal(AgentFailureReason.ToolCallsExceeded, Assert.IsType<AgentTurnFailed>(outcome).Reason);
        Assert.Empty(_ledger.ToolCalls);
    }

    [Fact]
    public async Task AnInvalidResponseIsRetriedOnce()
    {
        _model.Enqueue(new AiInvalidResponseException("max_tokens") { Usage = new AiModelUsage(10, 16_000, null, null), ServedModel = "claude-opus-5-5" });
        _model.Enqueue(Reply("Listo."));

        var outcome = await RunAsync(Context());

        Assert.IsType<AgentReplied>(outcome);
        Assert.Equal(2, _model.Requests.Count);
    }

    [Fact]
    public async Task TwoInvalidResponsesFailTheAgent()
    {
        _model.Enqueue(new AiInvalidResponseException("max_tokens"));
        _model.Enqueue(new AiInvalidResponseException("empty"));

        var outcome = await RunAsync(Context());

        var failed = Assert.IsType<AgentTurnFailed>(outcome);
        Assert.Equal((AgentFailureKind.AgentFailed, AgentFailureReason.InvalidResponse), (failed.Kind, failed.Reason));
    }

    [Fact]
    public async Task AReplyThatCopiesTheInstructionsCountsAsInvalid()
    {
        var instructions = string.Concat(Enumerable.Repeat("Nunca ofrezcas descuentos sin aprobación del gerente. ", 6));
        _model.Enqueue(Reply($"Mis instrucciones dicen: {instructions}"));
        _model.Enqueue(Reply("No puedo compartir eso."));

        var outcome = await RunAsync(Context() with { Config = Config() with { Instructions = instructions } });

        Assert.Equal("No puedo compartir eso.", Assert.IsType<AgentReplied>(outcome).Text);
    }

    [Fact]
    public async Task EveryModelCallLeavesItsUsageWithProviderCacheAndServedModelAlsoTheDiscardedOnes()
    {
        _model.Enqueue(new AiInvalidResponseException("max_tokens") { Usage = new AiModelUsage(10, 16_000, null, null), ServedModel = "claude-opus-5-5" });
        _model.Enqueue(Tools(Call("call_1", AgentToolNames.SearchCatalog, new { query = "x", limit = 1 })));
        _model.Enqueue(Reply("Listo."));

        await RunAsync(Context());

        Assert.Equal(
            [AiRunOutcomes.InvalidResponse, AiRunOutcomes.Ok, AiRunOutcomes.Ok],
            _ledger.Usage.Select(record => record.Outcome));
        Assert.All(_ledger.Usage, record => Assert.Equal(AiProviders.Anthropic, record.Provider));
        Assert.Equal(new AiModelUsage(1000, 50, 900, 100), _ledger.Usage[1].Usage);
        Assert.Equal("claude-opus-5", _ledger.Usage[1].ServedModel);
        Assert.All(_ledger.Usage, record => Assert.Equal(Context().JobId, record.JobId));
    }

    [Theory]
    [InlineData(typeof(AiQuotaExhaustedException), AiCredentialFailures.QuotaExhausted, AiModelErrorCodes.QuotaExhausted)]
    [InlineData(typeof(AiCredentialsRejectedException), AiCredentialFailures.CredentialsRejected, AiModelErrorCodes.CredentialsRejected)]
    [InlineData(typeof(AiPermissionDeniedException), AiCredentialFailures.PermissionDenied, AiModelErrorCodes.PermissionDenied)]
    public async Task AnAccountErrorLeavesTheIssueOnlyOnThatProvidersConnection(Type error, string failureCode, string code)
    {
        _model.Enqueue((AiModelException)Activator.CreateInstance(error, [null, null])!);

        var outcome = await RunAsync(Context());

        var failed = Assert.IsType<AgentTurnFailed>(outcome);
        Assert.Equal((AgentFailureKind.ProviderFailed, code), (failed.Kind, failed.Code));
        Assert.Equal([(Tenant, ConnectionId, failureCode)], _credentials.Marked);
        Assert.Single(_model.Requests);
    }

    [Fact]
    public async Task AModelErrorFailsTheAgentWithoutTouchingTheConnection()
    {
        _model.Enqueue(new AiModelUnavailableException());

        var outcome = await RunAsync(Context());

        var failed = Assert.IsType<AgentTurnFailed>(outcome);
        Assert.Equal((AgentFailureKind.AgentFailed, AiModelErrorCodes.ModelUnavailable), (failed.Kind, failed.Code));
        Assert.Empty(_credentials.Marked);
    }

    [Fact]
    public async Task ARateLimitIsLeftForTheWorkerToRetryWithItsRetryAfter()
    {
        _model.Enqueue(new AiRateLimitedException(TimeSpan.FromSeconds(9)));

        var outcome = await RunAsync(Context());

        var retry = Assert.IsType<AgentTurnRetry>(outcome);
        Assert.Equal((AiModelErrorCodes.RateLimited, TimeSpan.FromSeconds(9)), (retry.Code, retry.RetryAfter));
        Assert.Empty(_credentials.Marked);
    }

    [Fact]
    public async Task OnlyKnownSourcesSurvive()
    {
        _model.Enqueue(Tools(Call("call_1", AgentToolNames.SearchCatalog, new { query = "x", limit = 1 })));
        _model.Enqueue(Reply("Sí.", "envios", "call_1", "inventada", "qr-1"));

        var outcome = await RunAsync(Context() with { KnownSourceIds = ["envios", "qr-1"] });

        Assert.Equal(["envios", "call_1", "qr-1"], Assert.IsType<AgentReplied>(outcome).SourceIds);
    }

    [Fact]
    public async Task AHandoffEndsTheTurnWithItsReason()
    {
        _model.Enqueue(Tools(Call("call_h", AgentToolNames.HandoffToHuman, new { reason = "complaint", note = "Llegó roto." })));

        var outcome = await RunAsync(Context());

        Assert.Equal(new AgentHandedOff("complaint", "Llegó roto."), outcome);
    }

    [Fact]
    public async Task TheCustomerOfTheContextReachesTheToolsNotOneFromTheModel()
    {
        var orders = new FakeOrders();
        var customer = Guid.NewGuid();
        _model.Enqueue(Tools(Call("call_o", AgentToolNames.GetOrderStatus, new { orderNumber = "P-10", customerId = Guid.NewGuid() })));
        _model.Enqueue(Reply("Tu pedido va en camino."));

        await RunAsync(Context() with { CustomerId = customer }, tools: [new GetOrderStatusTool(orders)]);

        Assert.Equal([(customer, "P-10")], orders.Asked);
    }

    [Fact]
    public async Task WithoutACustomerTheOrderToolSaysSo()
    {
        _model.Enqueue(Tools(Call("call_o", AgentToolNames.GetOrderStatus, new { orderNumber = (string?)null })));
        _model.Enqueue(Reply("Necesito tus datos."));

        await RunAsync(Context(), tools: [new GetOrderStatusTool(new FakeOrders())]);

        var result = Assert.Single(Assert.Single(_model.Requests[1].PriorAssistantBlocks).ToolResults);
        Assert.True(result.IsError);
        Assert.Contains(AgentToolErrors.CustomerUnknown, result.ContentJson, StringComparison.Ordinal);
    }

    // Spec 2026-11-23 D-14: la llave de la reserva lleva el ordinal de las reservas que ya devolvieron una cita en este trabajo.
    [Fact]
    public async Task TwoBookingsInOneJobGetOrdinalsOneAndTwo()
    {
        var book = new ScriptedTool(AgentToolNames.BookAppointment, AiServicePrincipal.SchedulingBookOwn, AgentToolResult.Ok(new { appointmentId = "a" }));
        _model.Enqueue(Tools(Call("c1", AgentToolNames.BookAppointment, new { })));
        _model.Enqueue(Tools(Call("c2", AgentToolNames.BookAppointment, new { })));
        _model.Enqueue(Reply("Listo."));

        await RunAsync(Context(), tools: [book]);

        Assert.Equal([1, 2], book.Invocations.Select(invocation => invocation.BookingOrdinal));
    }

    [Fact]
    public async Task AFailedBookingDoesNotConsumeAnOrdinal()
    {
        var book = new ScriptedTool(
            AgentToolNames.BookAppointment, AiServicePrincipal.SchedulingBookOwn,
            AgentToolResult.Error(AgentToolErrors.SlotTaken), AgentToolResult.Ok(new { appointmentId = "a" }));
        _model.Enqueue(Tools(Call("c1", AgentToolNames.BookAppointment, new { })));
        _model.Enqueue(Tools(Call("c2", AgentToolNames.BookAppointment, new { })));
        _model.Enqueue(Reply("Listo."));

        await RunAsync(Context(), tools: [book]);

        Assert.Equal([1, 1], book.Invocations.Select(invocation => invocation.BookingOrdinal));
    }

    // La respuesta que sigue a una escritura exitosa de la Agenda sale marcada: el procesador no la descarta por un turno nuevo del cliente.
    [Fact]
    public async Task AReplyAfterASuccessfulSchedulingWriteIsMarkedWrote()
    {
        var book = new ScriptedTool(AgentToolNames.BookAppointment, AiServicePrincipal.SchedulingBookOwn, AgentToolResult.Ok(new { appointmentId = "a" }));
        _model.Enqueue(Tools(Call("c1", AgentToolNames.BookAppointment, new { })));
        _model.Enqueue(Reply("Listo."));

        var outcome = await RunAsync(Context(), tools: [book]);

        Assert.True(Assert.IsType<AgentReplied>(outcome).Wrote);
    }

    [Fact]
    public async Task AReplyAfterAFailedSchedulingWriteIsNotMarkedWrote()
    {
        var book = new ScriptedTool(AgentToolNames.BookAppointment, AiServicePrincipal.SchedulingBookOwn, AgentToolResult.Error(AgentToolErrors.SlotTaken));
        _model.Enqueue(Tools(Call("c1", AgentToolNames.BookAppointment, new { })));
        _model.Enqueue(Reply("Esa hora ya no está."));

        var outcome = await RunAsync(Context(), tools: [book]);

        Assert.False(Assert.IsType<AgentReplied>(outcome).Wrote);
    }

    // Revisión final W1: la escritura exitosa ya está hecha aunque el turno termine por un traspaso de otra herramienta o por un tope.
    [Fact]
    public async Task AToolHandoffAfterASuccessfulWriteIsMarkedWrote()
    {
        var book = new ScriptedTool(AgentToolNames.BookAppointment, AiServicePrincipal.SchedulingBookOwn, AgentToolResult.Ok(new { appointmentId = "a" }));
        var cancel = new ScriptedTool(
            AgentToolNames.CancelAppointment, AiServicePrincipal.SchedulingChangeOwn,
            AgentToolResult.Error(AgentToolErrors.SlotTaken) with { Handoff = new AgentToolHandoff(AgentHandoffCodes.AppointmentChangeBlocked, null) });
        _model.Enqueue(Tools(Call("c1", AgentToolNames.BookAppointment, new { })));
        _model.Enqueue(Tools(Call("c2", AgentToolNames.CancelAppointment, new { })));

        var outcome = await RunAsync(Context(), tools: [book, cancel]);

        Assert.True(Assert.IsType<AgentHandedOff>(outcome).Wrote);
    }

    [Fact]
    public async Task AFailureAfterASuccessfulWriteIsMarkedWrote()
    {
        var book = new ScriptedTool(AgentToolNames.BookAppointment, AiServicePrincipal.SchedulingBookOwn, AgentToolResult.Ok(new { appointmentId = "a" }));
        _model.Enqueue(Tools(Call("c1", AgentToolNames.BookAppointment, new { })));
        _model.Enqueue(Tools(Call("c2", AgentToolNames.SearchCatalog, new { query = "x", limit = 1 })));

        var outcome = await RunAsync(Context(), Limits() with { MaxModelCalls = 2 }, [book, new SearchCatalogTool(_catalog)]);

        var failed = Assert.IsType<AgentTurnFailed>(outcome);
        Assert.Equal(AgentFailureReason.ModelCallsExceeded, failed.Reason);
        Assert.True(failed.Wrote);
    }

    [Fact]
    public async Task AFailureWithoutAWriteIsNotMarkedWrote()
    {
        _model.Enqueue(Tools(Call("c1", AgentToolNames.SearchCatalog, new { query = "x", limit = 1 })));

        var outcome = await RunAsync(Context(), Limits() with { MaxModelCalls = 1 });

        Assert.False(Assert.IsType<AgentTurnFailed>(outcome).Wrote);
    }

    // Un trabajo retomado corre otra vez con un perfil nuevo: vuelve a contar desde 1 y encuentra la cita que ya hizo.
    [Fact]
    public async Task ResumedJobFindsTheBookingItAlreadyMade()
    {
        var book = new ScriptedTool(AgentToolNames.BookAppointment, AiServicePrincipal.SchedulingBookOwn, AgentToolResult.Ok(new { appointmentId = "a" }));
        var context = Context();
        _model.Enqueue(Tools(Call("c1", AgentToolNames.BookAppointment, new { })));
        _model.Enqueue(Reply("Listo."));
        _model.Enqueue(Tools(Call("c9", AgentToolNames.BookAppointment, new { })));
        _model.Enqueue(Reply("Listo."));

        await RunAsync(context, tools: [book]);
        await RunAsync(context, tools: [book]);

        Assert.Equal([1, 1], book.Invocations.Select(invocation => invocation.BookingOrdinal));
        Assert.Equal(["ai:" + context.JobId.ToString("N") + ":book:1"], book.Invocations.Select(i => $"ai:{i.JobId:N}:book:{i.BookingOrdinal}").Distinct());
    }

    [Fact]
    public async Task ASecondSchedulingWriteInOneResponseIsRejected()
    {
        var book = new ScriptedTool(AgentToolNames.BookAppointment, AiServicePrincipal.SchedulingBookOwn, AgentToolResult.Ok(new { appointmentId = "a" }));
        var cancel = new ScriptedTool(AgentToolNames.CancelAppointment, AiServicePrincipal.SchedulingChangeOwn, AgentToolResult.Ok(new { }));
        _model.Enqueue(Tools(Call("c1", AgentToolNames.BookAppointment, new { }), Call("c2", AgentToolNames.CancelAppointment, new { })));
        _model.Enqueue(Tools(Call("c3", AgentToolNames.BookAppointment, new { })));
        _model.Enqueue(Reply("Listo."));

        await RunAsync(Context(), tools: [book, cancel]);

        Assert.Empty(cancel.Invocations);
        Assert.Equal(AgentToolErrors.OneWritePerResponse, _ledger.ToolCalls.Single(call => call.CallId == "c2").ErrorCode);
        Assert.Equal([1, 2], book.Invocations.Select(invocation => invocation.BookingOrdinal));
    }

    [Fact]
    public async Task TheInvocationCarriesAgentAndConversation()
    {
        var read = new ScriptedTool(AgentToolNames.ListMyAppointments, AiServicePrincipal.SchedulingReadOwn, AgentToolResult.Ok(new { }));
        var context = Context();
        _model.Enqueue(Tools(Call("c1", AgentToolNames.ListMyAppointments, new { })));
        _model.Enqueue(Reply("Listo."));

        await RunAsync(context, tools: [read]);

        var invocation = Assert.Single(read.Invocations);
        Assert.Equal((context.AgentId, context.ConversationId), (invocation.AgentId, invocation.ConversationId));
    }

    // Spec 2026-11-17 D-B8: finish_objective es una herramienta final sólo en modo bloque.
    [Fact]
    public async Task FinishObjectiveIsOfferedOnlyInBlockMode()
    {
        _model.Enqueue(Reply("Hola."));
        _model.Enqueue(Reply("Hola."));

        await RunAsync(Context());
        await RunAsync(Context() with { Block = new AgentBlockContext("Resuelve dudas.", "") });

        Assert.DoesNotContain(_model.Requests[0].Tools, tool => tool.Name == AgentToolNames.FinishObjective);
        var finish = Assert.Single(_model.Requests[1].Tools, tool => tool.Name == AgentToolNames.FinishObjective);
        Assert.Contains("\"summary\"", finish.InputSchemaJson, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\":false", finish.InputSchemaJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinishObjectiveEndsTheTurnWithTheSummary()
    {
        _model.Enqueue(Final(AgentToolNames.FinishObjective, new { summary = "  El cliente quiere 3 jabones.  " }));

        var outcome = await RunAsync(Context() with { Block = new AgentBlockContext("Resuelve dudas.", "") });

        Assert.Equal("El cliente quiere 3 jabones.", Assert.IsType<AgentFinished>(outcome).Summary);
        Assert.Single(_model.Requests);
    }

    // §5.1: el resumen mide a lo sumo 500. Uno más largo es una respuesta inválida: se reintenta una vez y después falla.
    [Fact]
    public async Task ASummaryOver500IsAnInvalidArgument()
    {
        _model.Enqueue(Final(AgentToolNames.FinishObjective, new { summary = new string('x', 501) }));
        _model.Enqueue(Final(AgentToolNames.FinishObjective, new { summary = new string('x', 501) }));

        var outcome = await RunAsync(Context() with { Block = new AgentBlockContext("Resuelve dudas.", "") });

        Assert.Equal(AgentFailureReason.InvalidResponse, Assert.IsType<AgentTurnFailed>(outcome).Reason);
        Assert.Equal(2, _model.Requests.Count);
    }

    // Fuera del bloque, finish_objective no es una herramienta: el bucle la trata como desconocida y sigue.
    [Fact]
    public async Task FinishObjectiveOutsideABlockIsAnUnknownTool()
    {
        _model.Enqueue(Final(AgentToolNames.FinishObjective, new { summary = "Listo." }));
        _model.Enqueue(Reply("Hola."));

        var outcome = await RunAsync(Context());

        Assert.IsType<AgentReplied>(outcome);
        Assert.Equal(AgentToolErrors.UnknownTool, Assert.Single(_ledger.ToolCalls).ErrorCode);
    }

    private static AiModelResponse Final(string name, object arguments) =>
        new(null, [Call("final", name, arguments)], [], AiStopReason.ToolUse, new AiModelUsage(1200, 30, null, 1000), "claude-opus-5-5", "req_3");

    private Task<AgentTurnOutcome> RunAsync(AgentTurnContext context, AiRunLimits? limits = null, IEnumerable<IAgentTool>? tools = null) =>
        new AgentTurnRunner(
                _resolver,
                tools ?? [new SearchCatalogTool(_catalog), new GetOrderStatusTool(new FakeOrders())],
                _ledger,
                _credentials,
                limits ?? Limits(),
                new FixedClock(),
                NullLogger<AgentTurnRunner>.Instance)
            .RunAsync(context, Prompt(), TestContext.Current.CancellationToken);

    private static AiRunLimits Limits() => new(16_000, TimeSpan.FromSeconds(120), 6, 8, 30, null);

    private static AgentConfig Config() =>
        AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")) with
        {
            Instructions = "Atiende la tienda.",
            Tools = new AgentTools(Catalog: true, OrderStatus: true, QuotationDraft: false),
        };

    private static readonly Guid JobId = Guid.Parse("01900000-0000-7000-8000-0000000000a1");

    private static AgentTurnContext Context() =>
        new(
            Tenant, Guid.NewGuid(), Guid.NewGuid(), AiProviders.Anthropic, "claude-opus-5-5", "medium", Config(),
            new AiCredential(ConnectionId, "sk-ant-api03-TEST"), JobId, Guid.NewGuid(), Guid.NewGuid(), null, null, [], false);

    private static AgentPrompt Prompt() => new("sistema", null, "ahora", [new AiChatMessage(AiChatRole.User, "hola")]);

    private static AiToolCall Call(string id, string name, object arguments) => new(id, name, JsonSerializer.Serialize(arguments));

    private static AiModelResponse Tools(params AiToolCall[] calls) =>
        new(null, calls, [new AiOpaqueBlock("opaque-1")], AiStopReason.ToolUse, new AiModelUsage(1000, 50, 900, 100), "claude-opus-5", "req_1");

    private static AiModelResponse Reply(string text, params string[] sources) =>
        new(
            text,
            [Call("final", AgentToolNames.SendReply, new { text, sourceIds = sources })],
            [],
            AiStopReason.ToolUse,
            new AiModelUsage(1200, 30, null, 1000),
            "claude-opus-5-5",
            "req_2");

    /// <summary>Una herramienta que anota cada invocación y responde según un guion (la última se repite).</summary>
    private sealed class ScriptedTool(string name, string capability, params AgentToolResult[] script) : IAgentTool
    {
        public List<AgentToolInvocation> Invocations { get; } = [];

        public AiToolDefinition Definition { get; } = new(name, "falsa", """{"type":"object","properties":{},"required":[],"additionalProperties":false}""");

        public string Capability => capability;

        public bool IsOfferedTo(AgentTools tools, bool isTest) => true;

        public Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            return Task.FromResult(script[Math.Min(Invocations.Count - 1, script.Length - 1)]);
        }
    }

    private sealed class FakeModelClient : IAiModelClient
    {
        private readonly Queue<object> _script = new();

        public List<AiModelRequest> Requests { get; } = [];

        public void Enqueue(object step) => _script.Enqueue(step);

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _script.Dequeue() switch
            {
                AiModelResponse response => Task.FromResult(response),
                AiModelException failure => throw failure,
                var other => throw new InvalidOperationException($"Unexpected step {other}."),
            };
        }
    }

    private sealed class FakeResolver(IAiModelClient client) : IAiModelClientResolver
    {
        public List<AiProvider> Asked { get; } = [];

        public IAiModelClient For(AiProvider provider)
        {
            Asked.Add(provider);
            return client;
        }
    }

    private sealed class FakeLedger : IAiRunLedger
    {
        public List<AiUsageRecord> Usage { get; } = [];

        public List<AiToolCallRecord> ToolCalls { get; } = [];

        public Task RecordUsageAsync(AiUsageRecord record, CancellationToken cancellationToken)
        {
            Usage.Add(record);
            return Task.CompletedTask;
        }

        public Task RecordToolCallAsync(AiToolCallRecord record, CancellationToken cancellationToken)
        {
            ToolCalls.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCredentials : IAiProviderCredentials
    {
        public List<(Guid Tenant, Guid Connection, string Code)> Marked { get; } = [];

        public Task<AiCredentialResolution> ResolveAsync(Guid tenantId, AiProvider provider, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AiProviderConnectionState>> DescribeAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkNeedsAttentionAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken)
        {
            Marked.Add((tenantId, connectionId, failureCode));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCatalog : IAiCatalogSearch
    {
        public Task<IReadOnlyList<AiCatalogItem>> SearchAsync(Guid tenantId, string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiCatalogItem>>([new AiCatalogItem(Guid.NewGuid(), $"Producto {query}", "P-1", 12_000m, "COP", true, true)]);
    }

    private sealed class FakeOrders : IAiOrderStatusReader
    {
        public List<(Guid Customer, string? Number)> Asked { get; } = [];

        public Task<IReadOnlyList<AiOrderStatus>> ListAsync(
            Guid tenantId, Guid customerId, string? orderNumber, int limit, CancellationToken cancellationToken)
        {
            Asked.Add((customerId, orderNumber));
            return Task.FromResult<IReadOnlyList<AiOrderStatus>>([]);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);
    }
}
