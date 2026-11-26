using BuildingBlocks.Application;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Application.Http;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-11-17 (agente IA en chatbots) D-B17 y adenda D-3/D-4: el simulador nunca llama al modelo; se para en el bloque y el
/// tester elige la salida.</summary>
public sealed class SimulationAgentBlockTests
{
    private static readonly EngineSettings Settings = new("reiniciar", 3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly FlowDefinition Flow = FlowJson.Parse("""
        { "schema": 1, "start": { "firstNodeId": "ai" }, "variables": [],
          "nodes": [
            { "id": "ai", "kind": "aiAgent", "title": "Asesor IA", "agentId": "a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1",
              "objective": "Resuelve dudas.", "context": "", "saveTo": "asesor",
              "outputs": { "done": "bye", "human": "bye", "noAnswer": "bye", "idle": "bye", "error": "bye" } },
            { "id": "bye", "kind": "message", "title": "Despedida", "text": "Fin: {asesor.reason} {asesor.summary}", "outputs": { "next": null } }
          ] }
        """);

    private static RunState Initial() => RunState.Initial(FixedVariables.Build("Cliente de prueba", "3000000000", "Clasic", "Simulador"));

    private static SimulationRunner Runner() => new(new NoHttp(), new OpenHours(), new NoMedia(), new FixedClock());

    private static SimulationContext Context() => new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, false);

    [Fact]
    public async Task TheSimulatorStopsAtTheBlockWithoutCallingTheModel()
    {
        var (state, transcript) = await Runner().RunAsync(Flow, Settings, Initial(), new EngineInput.Start(), Context(), Ct);

        Assert.True(state.IsDelegated);
        Assert.Equal(new SimulationWaitingAtDto("ai", FlowNodeKinds.AiAgent), SimulationRunner.WaitingAtOf(state));
        var line = Assert.Single(transcript);
        Assert.Equal((TranscriptSenders.System, "ai"), (line.From, line.NodeId));
    }

    [Fact]
    public async Task AChosenOutputMovesTheSimulationAndSetsTheVariables()
    {
        var (waiting, _) = await Runner().RunAsync(Flow, Settings, Initial(), new EngineInput.Start(), Context(), Ct);

        var input = SimulationRunner.AgentResultInput(waiting, new SimulationAgentResultRequest("noAnswer", "No sabe", null, null));
        var (state, transcript) = await Runner().RunAsync(Flow, Settings, waiting, input, Context(), Ct);

        Assert.Equal(RunStatus.Completed, state.Status);
        // Sin reason, el motivo canónico de la salida (§6.3).
        Assert.Equal("Fin: no_answer No sabe", Assert.Single(transcript, entry => entry.From == TranscriptSenders.Bot).Text);
        Assert.Null(SimulationRunner.WaitingAtOf(state));
    }

    [Fact]
    public void AnAgentResultOutsideTheBlockIsRejected()
    {
        var exception = Assert.Throws<AutomationsDomainException>(() =>
            SimulationRunner.AgentResultInput(Initial(), new SimulationAgentResultRequest("done", null, null, null)));

        Assert.Equal("automations.simulation.not_waiting_at_agent", exception.Code);
    }

    [Theory]
    [InlineData("done", true)]
    [InlineData("noAnswer", true)]
    [InlineData("whatever", false)]
    public void TheAgentResultOutputIsOneOfTheFive(string output, bool valid)
    {
        var command = new SendSimulationMessageCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), null, null, null, new SimulationAgentResultRequest(output, null, null, null));

        Assert.Equal(valid, new SendSimulationMessageValidator().Validate(command).IsValid);
    }

    [Fact]
    public void AnAgentResultDoesNotTravelWithAMessage()
    {
        var command = new SendSimulationMessageCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), "Hola", null, null, new SimulationAgentResultRequest("done", null, null, null));

        Assert.False(new SendSimulationMessageValidator().Validate(command).IsValid);
    }

    private sealed class NoHttp : IHttpBlockExecutor
    {
        public Task<HttpCallResult> ExecuteAsync(HttpCallContext context, HttpNode node, RunVariables variables, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class OpenHours : IAutomationBusinessHours
    {
        public Task<IReadOnlyDictionary<Guid, string?>> ScheduleNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsOpenAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class NoMedia : IChatbotMediaStore
    {
        public Task<ChatbotMediaSession> OpenUploadAsync(
            Guid tenantId, string fileName, string contentType, long sizeBytes, long maxBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ChatbotMediaCompletion> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ChatbotMediaFile>> ResolveAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
