using BuildingBlocks.Application;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Application.Http;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-11-19 (chatbot CRM action) «Editor response and simulator»: the simulator never writes to the CRM. It says what it
/// would do in a system line and answers with the operation's success code.</summary>
public sealed class SimulationCrmActionTests
{
    private static readonly EngineSettings Settings = new("reiniciar", 3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FlowDefinition Flow(string operation) => FlowJson.Parse($$"""
        { "schema": 1, "start": { "firstNodeId": "crm" }, "variables": [],
          "nodes": [
            { "id": "crm", "kind": "crmAction", "title": "Acción", "operation": "{{operation}}",
              "tagId": "7a7a7a7a-7a7a-7a7a-7a7a-7a7a7a7a7a7a", "stageId": "5e5e5e5e-5e5e-5e5e-5e5e-5e5e5e5e5e5e", "saveTo": "crm",
              "outputs": { "ok": "check", "skipped": "other", "error": "other" } },
            { "id": "check", "kind": "condition", "title": "¿Hecho?", "match": "all",
              "rules": [ { "variable": "crm.result", "operator": "equals", "value": "{{CrmResultCodes.SuccessCodeOf(operation)}}" } ],
              "outputs": { "yes": "done", "no": "other" } },
            { "id": "done", "kind": "message", "title": "Hecho", "text": "Hecho: {crm.result}", "outputs": { "next": null } },
            { "id": "other", "kind": "message", "title": "Otro", "text": "Otro", "outputs": { "next": null } }
          ] }
        """);

    private static RunState Initial() => RunState.Initial(FixedVariables.Build("Cliente de prueba", "3000000000", "Clasic", "Simulador"));

    // The runner has no CRM dependency: constructing it without one is the proof that the simulator cannot write to the CRM.
    private static SimulationRunner Runner() => new(new NoHttp(), new OpenHours(), new NoMedia(), new FixedClock());

    private static SimulationContext Context() => new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, false);

    [Fact]
    public async Task NeverCallsAnyCrmPort()
    {
        var (state, transcript) = await Runner().RunAsync(Flow(CrmOperations.AddTag), Settings, Initial(), new EngineInput.Start(), Context(), Ct);

        Assert.Equal(RunStatus.Completed, state.Status);
        var line = Assert.Single(transcript, entry => entry.NodeId == "crm");
        Assert.Equal((TranscriptSenders.System, "Simulación: se etiquetaría al cliente"), (line.From, line.Text));
        Assert.Equal("Hecho: added", Assert.Single(transcript, entry => entry.From == TranscriptSenders.Bot).Text);
    }

    [Theory]
    [InlineData("addTag", "added", "Simulación: se etiquetaría al cliente")]
    [InlineData("removeTag", "removed", "Simulación: se quitaría la etiqueta al cliente")]
    [InlineData("moveDeal", "moved", "Simulación: se movería el negocio de la conversación")]
    public async Task WritesTheSuccessCodeForEachOperation(string operation, string code, string systemLine)
    {
        var (state, transcript) = await Runner().RunAsync(Flow(operation), Settings, Initial(), new EngineInput.Start(), Context(), Ct);

        Assert.Equal(code, state.Variables.TextOf("crm.result"));
        Assert.Equal(systemLine, Assert.Single(transcript, entry => entry.NodeId == "crm").Text);
        // The condition on {crm.result} picks the success branch.
        Assert.Equal($"Hecho: {code}", Assert.Single(transcript, entry => entry.From == TranscriptSenders.Bot).Text);
    }

    [Fact]
    public async Task CountsForTheReentryLimit()
    {
        var flow = FlowJson.Parse("""
            { "schema": 1, "start": { "firstNodeId": "crm" }, "variables": [],
              "nodes": [
                { "id": "crm", "kind": "crmAction", "title": "Etiquetar", "operation": "addTag",
                  "tagId": "7a7a7a7a-7a7a-7a7a-7a7a-7a7a7a7a7a7a", "stageId": null, "saveTo": "crm",
                  "outputs": { "ok": "crm", "skipped": null, "error": null } }
              ] }
            """);

        var (state, transcript) = await Runner().RunAsync(flow, Settings, Initial(), new EngineInput.Start(), Context(), Ct);

        Assert.Equal(RunStatus.Failed, state.Status);
        Assert.Equal("Terminó: el flujo da vueltas sin esperar al cliente", transcript[^1].Text);
        Assert.Equal(EngineReentries.Max, transcript.Count(entry => entry.Text.StartsWith("Simulación:", StringComparison.Ordinal)));
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
