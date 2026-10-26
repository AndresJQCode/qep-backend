using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Application.Http;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>
/// Spec 2026-10-21 §8 y D-S1..D-S3: el simulador corre el mismo <see cref="FlowEngine"/> sobre el borrador y anota la transcripción en vez
/// de mandar mensajes. Sin «API real», el bloque HTTP usa la respuesta de ejemplo del nodo y nunca llama al ejecutor; con ella, llama al
/// ejecutor con el id de la simulación y sin corrida. Pasar a un asesor sólo escribe una línea de sistema.
/// </summary>
public sealed class SimulationRunnerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SimulationId = Guid.CreateVersion7();
    private static readonly EngineSettings Settings = new("reiniciar", 3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SimulationContext Context(bool liveHttp = false) => new(TenantId, SimulationId, Guid.Empty, liveHttp);

    private static RunState Initial() =>
        RunState.Initial(FixedVariables.Build("Cliente de prueba", "3000000000", "Clasic", "Simulador"));

    /// <summary>Un bloque HTTP con su respuesta de ejemplo (o sin ella) que sigue a un mensaje por cada salida.</summary>
    private static FlowDefinition HttpFlow(string? sampleResponse) => FlowJson.Parse($$"""
        { "schema": 1, "start": { "firstNodeId": "n3" }, "variables": [],
          "nodes": [
            { "id": "n3", "kind": "http", "title": "Buscar cliente", "connectionId": "11111111-1111-1111-1111-111111111111", "method": "GET", "path": "/customers",
              "mapping": [ { "from": "data[0]", "to": "cliente" } ]{{(sampleResponse is null ? string.Empty : $", \"sampleResponse\": {sampleResponse}")}},
              "outputs": { "ok": "n4", "error": "n5" } },
            { "id": "n4", "kind": "message", "title": "Encontrado", "text": "Cliente {cliente.id}", "outputs": { "next": null } },
            { "id": "n5", "kind": "message", "title": "Falló", "text": "No pudimos buscarte.", "outputs": { "next": null } }
          ] }
        """);

    [Fact]
    public async Task StartTranscribesTheBotMessagesAndStopsAtTheWait()
    {
        var http = new RecordingHttpBlocks(null);

        var (state, transcript) = await Runner(http).RunAsync(AnnexAFlow.Parse(), Settings, Initial(), new EngineInput.Start(), Context(), Ct);

        Assert.Equal(["bot", "bot"], transcript.Select(entry => entry.From));
        Assert.Equal(["Bienvenida", "Número de documento"], transcript.Select(entry => entry.NodeTitle));
        Assert.Equal(["n1", "n2"], transcript.Select(entry => entry.NodeId));
        Assert.Equal("Hola Cliente de prueba, bienvenido a la línea de servicio.", transcript[0].Text);
        // n1 lleva urlButton: sale como interactivo con un botón que abre la URL, y la transcripción lo trae para pintarlo.
        Assert.Equal(new TranscriptUrlButton("Registrarme", "https://www.example.com/registro"), transcript[0].UrlButton);
        Assert.Equal("n2", state.CurrentNodeId);
        Assert.Equal(RunStatus.Waiting, state.Status);
        Assert.Empty(http.Calls);
    }

    [Fact]
    public async Task HttpUsesTheSampleResponseWithoutTheExecutor()
    {
        var http = new RecordingHttpBlocks(null);

        var (state, transcript) = await Runner(http).RunAsync(
            HttpFlow("""{ "data": [ { "id": 7 } ] }"""), Settings, Initial(), new EngineInput.Start(), Context(), Ct);

        Assert.Equal("7", state.Variables.TextOf("cliente.id"));
        Assert.Equal("Cliente 7", Assert.Single(transcript, entry => entry.From == "bot").Text);
        Assert.Empty(http.Calls);
    }

    [Fact]
    public async Task LiveHttpCallsTheExecutorWithTheSimulationId()
    {
        var mapped = RunVariables.Empty();
        mapped.Set("cliente", new JsonObject { ["id"] = 9 });
        var http = new RecordingHttpBlocks(new HttpCallResult(true, 200, mapped, HttpCallOutcomes.Ok, null));

        var (state, transcript) = await Runner(http).RunAsync(
            HttpFlow("""{ "data": [ { "id": 7 } ] }"""), Settings, Initial(), new EngineInput.Start(), Context(liveHttp: true), Ct);

        var call = Assert.Single(http.Calls);
        Assert.Equal(new HttpCallContext(TenantId, RunId: null, SimulationId, "n3"), call.Context);
        Assert.Equal("Cliente de prueba", call.Variables.TextOf("nombre"));
        Assert.Equal("9", state.Variables.TextOf("cliente.id"));
        Assert.Equal("Cliente 9", Assert.Single(transcript, entry => entry.From == "bot").Text);
    }

    [Fact]
    public async Task WithoutSampleResponseTheErrorOutputIsTakenWithASystemLine()
    {
        var http = new RecordingHttpBlocks(null);

        var (state, transcript) = await Runner(http).RunAsync(HttpFlow(null), Settings, Initial(), new EngineInput.Start(), Context(), Ct);

        Assert.Equal(["system", "bot", "system"], transcript.Select(entry => entry.From));
        Assert.Equal("Este bloque no tiene respuesta de ejemplo", transcript[0].Text);
        Assert.Equal("n3", transcript[0].NodeId);
        Assert.Equal("Buscar cliente", transcript[0].NodeTitle);
        Assert.Equal("No pudimos buscarte.", transcript[1].Text);
        // El borrador no se valida en el runner: una salida sin conectar termina con su línea.
        Assert.Equal("Terminó: salida sin conectar", transcript[2].Text);
        Assert.False(state.Variables.Has("cliente"));
        Assert.Empty(http.Calls);
    }

    [Fact]
    public async Task HandoffWritesASystemLineAndEnds()
    {
        var flow = FlowJson.Parse("""
            { "schema": 1, "start": { "firstNodeId": "n9" }, "variables": [],
              "nodes": [ { "id": "n9", "kind": "handoff", "title": "Pasar a un asesor", "text": "Te paso con un asesor." } ] }
            """);

        var (state, transcript) = await Runner(new RecordingHttpBlocks(null)).RunAsync(flow, Settings, Initial(), new EngineInput.Start(), Context(), Ct);

        Assert.Equal(["bot", "system"], transcript.Select(entry => entry.From));
        Assert.Equal("Te paso con un asesor.", transcript[0].Text);
        Assert.Equal("Pasaría la conversación a la bandeja, sin asignar", transcript[1].Text);
        Assert.Equal("Pasar a un asesor", transcript[1].NodeTitle);
        Assert.Equal(RunStatus.HandedOff, state.Status);
    }

    /// <summary>Spec 2026-10-25 D-4: el simulador dice adónde iría el traspaso: a la bandeja sin asignar o al miembro elegido.</summary>
    [Fact]
    public async Task TheSimulatorSaysWhereTheHandoffGoes()
    {
        var unassigned = FlowJson.Parse("""
            { "schema": 1, "start": { "firstNodeId": "n9" }, "variables": [],
              "nodes": [ { "id": "n9", "kind": "handoff", "title": "Pasar a un asesor", "text": null } ] }
            """);
        var assigned = FlowJson.Parse($$"""
            { "schema": 1, "start": { "firstNodeId": "n9" }, "variables": [],
              "nodes": [ { "id": "n9", "kind": "handoff", "title": "Pasar a un asesor", "text": null, "memberId": "{{Guid.CreateVersion7()}}" } ] }
            """);

        var (_, withoutMember) = await Runner(new RecordingHttpBlocks(null)).RunAsync(unassigned, Settings, Initial(), new EngineInput.Start(), Context(), Ct);
        var (_, withMember) = await Runner(new RecordingHttpBlocks(null)).RunAsync(assigned, Settings, Initial(), new EngineInput.Start(), Context(), Ct);

        Assert.Equal("Pasaría la conversación a la bandeja, sin asignar", withoutMember.Single(entry => entry.From == "system").Text);
        Assert.Equal("Pasaría la conversación al miembro elegido", withMember.Single(entry => entry.From == "system").Text);
    }

    /// <summary>El motor corta un ciclo dentro de un recorrido (P2-R13), pero no uno que pasa por un bloque HTTP: cada resultado es un
    /// recorrido nuevo. Con la respuesta de ejemplo no hay espera de red, así que sin tope el simulador daría vueltas para siempre.</summary>
    [Fact]
    public async Task AnHttpCycleEndsFailedWithALoopLine()
    {
        var flow = FlowJson.Parse("""
            { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
              "nodes": [
                { "id": "n1", "kind": "http", "title": "Consultar", "connectionId": "11111111-1111-1111-1111-111111111111", "method": "GET", "path": "/status",
                  "mapping": [], "sampleResponse": { "ok": true }, "outputs": { "ok": "n1", "error": null } }
              ] }
            """);

        var (state, transcript) = await Runner(new RecordingHttpBlocks(null)).RunAsync(flow, Settings, Initial(), new EngineInput.Start(), Context(), Ct);

        Assert.Equal(RunStatus.Failed, state.Status);
        var line = Assert.Single(transcript);
        Assert.Equal("system", line.From);
        Assert.Equal("Terminó: el flujo da vueltas sin esperar al cliente", line.Text);
    }

    private static SimulationRunner Runner(IHttpBlockExecutor http) => new(http, new ClosedHours(), new FixedClock());

    private sealed class RecordingHttpBlocks(HttpCallResult? result) : IHttpBlockExecutor
    {
        public List<(HttpCallContext Context, RunVariables Variables)> Calls { get; } = [];

        public Task<HttpCallResult> ExecuteAsync(HttpCallContext context, HttpNode node, RunVariables variables, CancellationToken cancellationToken)
        {
            Calls.Add((context, variables));
            return Task.FromResult(result ?? throw new NotSupportedException("La prueba no esperaba que el simulador llamara a la API."));
        }
    }

    private sealed class ClosedHours : IAutomationBusinessHours
    {
        public Task<IReadOnlyDictionary<Guid, string?>> ScheduleNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsOpenAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }
}
