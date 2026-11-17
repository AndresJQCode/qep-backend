using Microsoft.Extensions.Logging.Abstractions;
using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Plan fase 2, C2.2: el turno del copiloto sobre el bucle común, con un ejecutor de herramientas falso.</summary>
public sealed class CopilotTurnRunnerTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Member = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    private static readonly Guid ConnectionId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly Guid OrderA = Guid.Parse("01900000-0000-7000-8000-00000000a0a0");
    private static readonly Guid OrderB = Guid.Parse("01900000-0000-7000-8000-00000000b0b0");

    private readonly LoopModelClient _model = new();
    private readonly LoopLedger _ledger = new();
    private readonly LoopCredentials _credentials = new();
    private readonly FakeCopilotTools _tools = new();
    private readonly List<string> _progress = [];

    // D-C6: la cita sale del ref que devolvió una herramienta de este trabajo; la etiqueta también. Una que el modelo inventa (o de otro
    // trabajo) se descarta, y nunca hay una URL.
    [Fact]
    public async Task AnswerKeepsOnlyCitationsFromThisJobsTools()
    {
        _tools.Results["search_orders"] = CopilotToolResult.Ok(
            new { items = new[] { new { number = "P-1" } } },
            [new CopilotCitation("order", OrderA.ToString(), "P-1"), new CopilotCitation("order", OrderB.ToString(), "P-2")]);
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("call_1", "search_orders", new { query = "acme" })));
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("final", CopilotToolNames.Answer, new
        {
            text = "  Acme tiene el pedido P-1.  ",
            citations = new[] { OrderA.ToString(), Guid.NewGuid().ToString(), "https://evil.example/x", OrderA.ToString() },
        })));

        var outcome = await RunAsync();

        var answered = Assert.IsType<CopilotAnswered>(outcome);
        Assert.Equal("Acme tiene el pedido P-1.", answered.Text);
        Assert.Equal([new CopilotCitation("order", OrderA.ToString(), "P-1")], answered.Citations);
        Assert.Null(answered.CannotAnswerReason);
    }

    [Fact]
    public async Task CannotAnswerIsAnOrdinaryAnswer()
    {
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("final", CopilotToolNames.CannotAnswer, new
        {
            reason = "no_tool",
            text = "No tengo un reporte para eso.",
        })));

        var outcome = await RunAsync();

        var answered = Assert.IsType<CopilotAnswered>(outcome);
        Assert.Equal(("No tengo un reporte para eso.", "no_tool"), (answered.Text, answered.CannotAnswerReason));
        Assert.Empty(answered.Citations);
    }

    [Fact]
    public async Task ACannotAnswerWithAnUnknownReasonIsAnInvalidResponse()
    {
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("final", CopilotToolNames.CannotAnswer, new { reason = "lazy", text = "No." })));
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("final", CopilotToolNames.CannotAnswer, new { reason = "lazy", text = "No." })));

        var outcome = await RunAsync();

        Assert.Equal(AgentHandoffCodes.InvalidResponse, CopilotTurnRunner.ErrorCodeOf(outcome));
    }

    [Theory]
    [InlineData(8, 100, AgentHandoffCodes.ModelCallsExceeded)]
    [InlineData(100, 12, AgentHandoffCodes.ToolCallsExceeded)]
    public async Task TheLimitsEndTheJobWithTheirCodes(int maxModelCalls, int maxToolCalls, string code)
    {
        _tools.Results["search_orders"] = CopilotToolResult.Ok(new { items = Array.Empty<object>() }, []);
        for (var index = 0; index < 20; index++)
        {
            _model.Enqueue(LoopResponses.Tools(LoopResponses.Call($"call_{index}", "search_orders", new { query = "x" })));
        }

        var outcome = await RunAsync(new AiRunLimits(16_000, TimeSpan.FromSeconds(120), maxModelCalls, maxToolCalls, 20, null));

        Assert.Equal(code, CopilotTurnRunner.ErrorCodeOf(outcome));
    }

    [Fact]
    public async Task TheModelSeesOnlyTheAvailableToolsAndTheFinalsSortedByName()
    {
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("final", CopilotToolNames.Answer, new { text = "Hola.", citations = Array.Empty<string>() })));

        await RunAsync();

        Assert.Equal(
            [CopilotToolNames.Answer, CopilotToolNames.CannotAnswer, "get_orders_summary", "search_orders"],
            _model.Requests.Single().Tools.Select(tool => tool.Name));
        Assert.Equal([new CopilotPrincipal(Tenant, Member)], _tools.Listed);
    }

    // D-C3: el ejecutor ya revalidó los permisos; el bucle le pasa su error al modelo, que puede seguir con lo que tiene.
    [Fact]
    public async Task ANoPermissionToolErrorGoesBackToTheModel()
    {
        _tools.Results["search_orders"] = CopilotToolResult.Error(CopilotToolErrors.NoPermission);
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("call_1", "search_orders", new { query = "x" })));
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("final", CopilotToolNames.CannotAnswer, new { reason = "no_permission", text = "No puedes ver pedidos." })));

        var outcome = await RunAsync();

        var result = Assert.Single(Assert.Single(_model.Requests[1].PriorAssistantBlocks).ToolResults);
        Assert.True(result.IsError);
        Assert.Contains(CopilotToolErrors.NoPermission, result.ContentJson, StringComparison.Ordinal);
        Assert.Equal("no_permission", Assert.IsType<CopilotAnswered>(outcome).CannotAnswerReason);
    }

    [Fact]
    public async Task AMemberWithoutAnActiveMembershipNeverReachesTheModel()
    {
        _tools.Available = null;

        var outcome = await RunAsync();

        Assert.Equal(CopilotToolErrors.MemberInactive, CopilotTurnRunner.ErrorCodeOf(outcome));
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task AMemberDeactivatedBetweenCallsEndsTheJob()
    {
        _tools.Results["search_orders"] = CopilotToolResult.Error(CopilotToolErrors.MemberInactive);
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("call_1", "search_orders", new { query = "x" })));

        var outcome = await RunAsync();

        Assert.Equal(CopilotToolErrors.MemberInactive, CopilotTurnRunner.ErrorCodeOf(outcome));
        Assert.Single(_model.Requests);
    }

    [Fact]
    public async Task ProgressNamesEachToolBeforeItRuns()
    {
        _tools.Results["search_orders"] = CopilotToolResult.Ok(new { items = Array.Empty<object>() }, []);
        _tools.Results["get_orders_summary"] = CopilotToolResult.Ok(new { orderCount = 0 }, []);
        _tools.OnExecute = name => Assert.Equal(name, _progress[^1]);
        _model.Enqueue(LoopResponses.Tools(
            LoopResponses.Call("call_1", "search_orders", new { query = "x" }), LoopResponses.Call("call_2", "get_orders_summary", new { })));
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("final", CopilotToolNames.Answer, new { text = "Nada.", citations = Array.Empty<string>() })));

        await RunAsync();

        Assert.Equal(["search_orders", "get_orders_summary"], _progress);
    }

    // D-C12: el consumo es del hilo y del miembro, sin agente.
    [Fact]
    public async Task TheUsageIsOfTheThreadAndTheMember()
    {
        var thread = Guid.NewGuid();
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("final", CopilotToolNames.Answer, new { text = "Hola.", citations = Array.Empty<string>() })));

        await RunAsync(context: Context() with { ThreadId = thread });

        var usage = Assert.Single(_ledger.Usage);
        Assert.Equal((null, thread, Member), (usage.AgentId, usage.CopilotThreadId, usage.MemberUserId));
    }

    private static CopilotTurnContext Context() =>
        new(Tenant, Member, Guid.NewGuid(), Guid.NewGuid(), AiProviders.Anthropic, "claude-opus-5-5", "medium",
            new AiCredential(ConnectionId, "sk-ant-api03-TEST"));

    private Task<AgentTurnOutcome> RunAsync(AiRunLimits? limits = null, CopilotTurnContext? context = null) =>
        new CopilotTurnRunner(
                new LoopResolver(_model),
                _ledger,
                _credentials,
                _tools,
                new CopilotRunLimits(limits ?? new AiRunLimits(16_000, TimeSpan.FromSeconds(120), 8, 12, 20, null)),
                new LoopClock(),
                NullLogger<CopilotTurnRunner>.Instance)
            .RunAsync(
                context ?? Context(),
                new AgentPrompt("sistema", null, "ahora", [new AiChatMessage(AiChatRole.User, "¿cuánto vendimos?")]),
                (tool, _) =>
                {
                    _progress.Add(tool);
                    return Task.CompletedTask;
                },
                TestContext.Current.CancellationToken);
}

/// <summary>El ejecutor falso: <see cref="Available"/> es lo que el miembro puede usar (<c>null</c> sin membresía activa) y
/// <see cref="Results"/> lo que devuelve cada herramienta.</summary>
internal sealed class FakeCopilotTools : ICopilotToolExecutor
{
    public IReadOnlyList<AiToolDefinition>? Available { get; set; } =
    [
        new("search_orders", "Busca pedidos.", """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}"""),
        new("get_orders_summary", "Resumen.", """{"type":"object","properties":{},"required":[],"additionalProperties":false}"""),
    ];

    public Dictionary<string, CopilotToolResult> Results { get; } = new(StringComparer.Ordinal);

    public List<CopilotPrincipal> Listed { get; } = [];

    public List<(CopilotPrincipal Principal, string Tool, string Arguments)> Executed { get; } = [];

    public Action<string>? OnExecute { get; set; }

    public Task<IReadOnlyList<AiToolDefinition>?> ListAvailableAsync(CopilotPrincipal principal, CancellationToken cancellationToken)
    {
        Listed.Add(principal);
        return Task.FromResult(Available);
    }

    public Task<CopilotToolResult> ExecuteAsync(
        CopilotPrincipal principal, string toolName, string argumentsJson, CancellationToken cancellationToken)
    {
        OnExecute?.Invoke(toolName);
        Executed.Add((principal, toolName, argumentsJson));
        return Task.FromResult(Results[toolName]);
    }
}
