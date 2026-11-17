using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Plan fase 2, C2.1: el bucle común sin el agente. Lo que es del agente (sus finales, su principal de servicio) sigue probado
/// por <see cref="AgentTurnRunnerTests"/>, que no se toca: es la evidencia de la extracción.</summary>
public sealed class ModelToolLoopTests
{
    private readonly LoopModelClient _model = new();
    private readonly LoopLedger _ledger = new();

    [Fact]
    public async Task TheLoopStopsAtAFinalToolOfItsProfile()
    {
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("call_1", "lookup", new { q = "x" })));
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("call_2", "done", new { text = "Listo." })));
        var profile = new EchoProfile();

        var outcome = await Loop().RunAsync(Run(), Prompt(), profile, TestContext.Current.CancellationToken);

        Assert.Equal(new AgentFinished("Listo."), outcome);
        Assert.Equal(["call_1"], profile.Executed);
        Assert.Equal(["done", "lookup"], _model.Requests[0].Tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.Equal(2, _model.Requests.Count);
    }

    [Fact]
    public async Task TheUsageGoesToTheOwnerOfTheRun()
    {
        var thread = Guid.NewGuid();
        var member = Guid.NewGuid();
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("call_2", "done", new { text = "Listo." })));

        await Loop().RunAsync(
            Run() with { Owner = new AiUsageOwner(null, null, null, null, null, thread, member) }, Prompt(), new EchoProfile(),
            TestContext.Current.CancellationToken);

        var usage = Assert.Single(_ledger.Usage);
        Assert.Equal((null, thread, member), (usage.AgentId, usage.CopilotThreadId, usage.MemberUserId));
    }

    [Fact]
    public async Task TheLimitsOfTheRunApply()
    {
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("call_1", "lookup", new { q = "a" }), LoopResponses.Call("call_2", "lookup", new { q = "b" })));

        var outcome = await Loop().RunAsync(
            Run() with { Limits = new AiRunLimits(1000, TimeSpan.FromSeconds(120), 8, 1, 20, null) }, Prompt(), new EchoProfile(),
            TestContext.Current.CancellationToken);

        Assert.Equal(AgentFailureReason.ToolCallsExceeded, Assert.IsType<AgentTurnFailed>(outcome).Reason);
    }

    private ModelToolLoop Loop() =>
        new(new LoopResolver(_model), _ledger, new LoopCredentials(), new LoopClock(), NullLogger.Instance);

    private static ToolLoopRun Run() =>
        new(
            Guid.Parse("01900000-0000-7000-8000-000000000001"), Guid.NewGuid(), AiProviders.Anthropic, "claude-opus-5-5", "medium",
            new AiCredential(Guid.NewGuid(), "sk-ant-api03-TEST"), new AiRunLimits(16_000, TimeSpan.FromSeconds(120), 6, 8, 30, null),
            new AiUsageOwner(Guid.NewGuid(), null, null, null, Guid.NewGuid()));

    private static AgentPrompt Prompt() => new("sistema", null, "ahora", [new AiChatMessage(AiChatRole.User, "hola")]);

    /// <summary>Un perfil mínimo: <c>lookup</c> devuelve lo que recibe y <c>done</c> termina con su texto.</summary>
    private sealed class EchoProfile : IToolLoopProfile
    {
        public List<string> Executed { get; } = [];

        public IReadOnlyList<AiToolDefinition> Tools { get; } =
        [
            new("lookup", "Busca.", """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"],"additionalProperties":false}"""),
            new("done", "Termina.", """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}"""),
        ];

        public bool IsFinal(string toolName) => toolName == "done";

        public bool Writes(string toolName) => false;

        public AgentTurnOutcome? Finish(AiToolCall toolCall, IReadOnlySet<string> callIds) =>
            new AgentFinished(JsonDocument.Parse(toolCall.ArgumentsJson).RootElement.GetProperty("text").GetString()!);

        public Task<ToolLoopExecution> ExecuteAsync(AiToolCall toolCall, JsonElement arguments, CancellationToken cancellationToken)
        {
            Executed.Add(toolCall.Id);
            return Task.FromResult(new ToolLoopExecution(AgentToolResult.Ok(new { echo = arguments.GetProperty("q").GetString() })));
        }
    }
}
