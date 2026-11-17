using System.Text.Json.Nodes;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-11-17 (agente IA en chatbots) §5.1 y §5.2: el bloque <c>aiAgent</c> en el documento del flujo y en el validador.</summary>
public sealed class FlowAiAgentTests
{
    private static readonly Guid ActiveAgent = Guid.Parse("a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1");
    private static readonly Guid DraftAgent = Guid.Parse("d1d1d1d1-d1d1-d1d1-d1d1-d1d1d1d1d1d1");
    private static readonly Guid PausedAgent = Guid.Parse("b1b1b1b1-b1b1-b1b1-b1b1-b1b1b1b1b1b1");

    private const string Json = """
        {
          "schema": 1,
          "start": { "firstNodeId": "ai-agent-1" },
          "variables": [],
          "nodes": [
            { "id": "ai-agent-1", "kind": "aiAgent", "title": "Asesor IA",
              "agentId": "a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1",
              "objective": "Resuelve dudas de productos y precios.",
              "context": "El cliente se llama {nombre_contacto}.",
              "firstTurn": "nextMessage", "idleMinutes": 90, "saveTo": "asesorIa",
              "outputs": { "done": "end", "human": "end", "noAnswer": "end", "idle": null, "error": "end" } },
            { "id": "end", "kind": "handoff", "title": "Asesor", "text": null, "memberId": null, "outputs": {} }
          ]
        }
        """;

    [Fact]
    public void AnAiAgentNodeRoundTrips()
    {
        var flow = FlowJson.Parse(Json);
        var node = Assert.IsType<AiAgentNode>(flow.Nodes[0]);

        Assert.Equal(FlowNodeKinds.AiAgent, node.Kind);
        Assert.Equal(
            (ActiveAgent, "Resuelve dudas de productos y precios.", "El cliente se llama {nombre_contacto}.", "nextMessage", 90, "asesorIa"),
            (node.AgentId!.Value, node.Objective, node.Context, node.FirstTurn, node.IdleMinutes, node.SaveTo));
        Assert.Null(node.Outputs["idle"]);

        var serialized = FlowJson.Serialize(flow);
        Assert.Equal(serialized, FlowJson.Serialize(FlowJson.Parse(serialized)));
        var written = Assert.IsType<JsonObject>(JsonNode.Parse(serialized)!["nodes"]![0]);
        Assert.Equal("a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1", written["agentId"]!.GetValue<string>());
        Assert.Equal("nextMessage", written["firstTurn"]!.GetValue<string>());
        Assert.Equal(90, written["idleMinutes"]!.GetValue<int>());
        Assert.Equal("asesorIa", written["saveTo"]!.GetValue<string>());
    }

    // Un borrador recién creado trae agentId null; uno de antes del campo, sin firstTurn ni idleMinutes, se lee con los valores por defecto.
    [Fact]
    public void AnAiAgentWithoutOptionalFieldsReadsTheDefaults()
    {
        var json = Json
            .Replace("\"agentId\": \"a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1\",", "\"agentId\": null,", StringComparison.Ordinal)
            .Replace("\"firstTurn\": \"nextMessage\", \"idleMinutes\": 90, ", string.Empty, StringComparison.Ordinal);

        var node = Assert.IsType<AiAgentNode>(FlowJson.Parse(json).Nodes[0]);

        Assert.Equal(((Guid?)null, "lastMessage", 60), (node.AgentId, node.FirstTurn, node.IdleMinutes));
    }

    [Fact]
    public void AnUnknownFirstTurnIsMalformed()
    {
        var exception = Assert.Throws<FlowFormatException>(() =>
            FlowJson.Parse(Json.Replace("\"nextMessage\"", "\"whenever\"", StringComparison.Ordinal)));

        Assert.Equal("flow.malformed", exception.Code);
    }

    [Fact]
    public void TheBlockHasTheFiveOutputsInOrder()
    {
        Assert.Contains(FlowNodeKinds.AiAgent, FlowNodeKinds.All);
        Assert.Equal(["done", "human", "noAnswer", "idle", "error"], FlowNodeKinds.OutputsOf(Agent()));
        Assert.False(FlowNodeKinds.IsTerminal(FlowNodeKinds.AiAgent));
    }

    [Fact]
    public void AValidBlockOnlyWarnsAboutItsUnconnectedOutputs()
    {
        var issues = FlowValidator.Validate(Flow(Agent() with { Outputs = Out(("done", "end"), ("human", null), ("noAnswer", null), ("idle", null), ("error", null)) }), Ctx());

        Assert.DoesNotContain(issues, issue => issue.Severity == FlowIssueSeverity.Error);
        var unconnected = issues.Where(issue => issue.Code == "node.output_unconnected_optional").ToList();
        Assert.Equal(4, unconnected.Count);
        Assert.All(unconnected, issue => Assert.Equal((FlowIssueSeverity.Warning, "ai"), (issue.Severity, issue.NodeId)));
        Assert.Contains(unconnected, issue => issue.Message == "La salida «pidió un asesor» de «Asesor IA» no está conectada.");
    }

    [Fact]
    public void AMissingAgentIsAnError()
    {
        Assert.Equal(FlowIssueSeverity.Error, Single(FlowValidator.Validate(Flow(Agent() with { AgentId = null }), Ctx()), "ai_agent.agent_missing").Severity);
        Assert.Equal(FlowIssueSeverity.Error, Single(FlowValidator.Validate(Flow(Agent() with { AgentId = Guid.NewGuid() }), Ctx()), "ai_agent.agent_missing").Severity);
    }

    [Fact]
    public void ADraftAgentIsAnError() =>
        Assert.Equal(FlowIssueSeverity.Error, Single(FlowValidator.Validate(Flow(Agent() with { AgentId = DraftAgent }), Ctx()), "ai_agent.agent_not_published").Severity);

    [Fact]
    public void APausedAgentIsAWarning()
    {
        var issue = Single(FlowValidator.Validate(Flow(Agent() with { AgentId = PausedAgent }), Ctx()), "ai_agent.agent_paused");

        Assert.Equal(FlowIssueSeverity.Warning, issue.Severity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyObjectiveIsAnError(string objective) =>
        Assert.Equal(FlowIssueSeverity.Error, Single(FlowValidator.Validate(Flow(Agent() with { Objective = objective }), Ctx()), "ai_agent.objective_missing").Severity);

    [Fact]
    public void TheObjectiveAndTheContextHaveATopOf2000()
    {
        Assert.DoesNotContain(FlowValidator.Validate(Flow(Agent() with { Objective = new string('a', 2000), Context = new string('b', 2000) }), Ctx()), issue => issue.Code == "text.too_long");

        var issues = FlowValidator.Validate(Flow(Agent() with { Objective = new string('a', 2001), Context = new string('b', 2001) }), Ctx());

        Assert.Equal(2, issues.Count(issue => issue.Code == "text.too_long" && issue.Severity == FlowIssueSeverity.Error));
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(1380, false)]
    [InlineData(1381, true)]
    public void TheIdleMinutesGoFrom5To1380(int minutes, bool flagged)
    {
        var issues = FlowValidator.Validate(Flow(Agent() with { IdleMinutes = minutes }), Ctx());

        Assert.Equal(flagged, issues.Any(issue => issue.Code == "ai_agent.idle_out_of_range" && issue.Severity == FlowIssueSeverity.Error));
    }

    [Fact]
    public void WithoutTheAiModuleTheBlockIsAnError() =>
        Assert.Equal(FlowIssueSeverity.Error, Single(FlowValidator.Validate(Flow(Agent()), Ctx(aiEnabled: false)), "ai_agent.module_disabled").Severity);

    [Fact]
    public void OnInstagramTheBlockIsAnError() =>
        Assert.Equal(FlowIssueSeverity.Error, Single(FlowValidator.Validate(Flow(Agent()), Ctx(channel: "Instagram")), "ai_agent.channel_not_supported").Severity);

    [Fact]
    public void AnInvalidSaveToIsAnError() =>
        Assert.Contains(FlowValidator.Validate(Flow(Agent() with { SaveTo = "Asesor IA" }), Ctx()), issue => issue.Code == "variable.name_invalid");

    // §5.1: {saveTo}.summary, .reason y .quotationNumber cuentan como escritas y el editor las ofrece.
    [Fact]
    public void TheBlockVariablesAreKnownAndNeverFlaggedAsUnset()
    {
        var message = new MessageNode("m1", "Resumen", Out(("next", null)), "Resumen: {asesorIa.summary} ({asesorIa.reason}) {asesorIa.quotationNumber}", null);
        var flow = Flow(Agent() with { Outputs = Out(("done", "m1"), ("human", "end"), ("noAnswer", "end"), ("idle", "end"), ("error", "end")) }, message);

        Assert.DoesNotContain(FlowValidator.Validate(flow, Ctx()), issue => issue.Code == "variable.never_set");
        Assert.Equal(
            ["nombre_contacto", "telefono", "empresa", "conexion", "asesorIa.summary", "asesorIa.reason", "asesorIa.quotationNumber"],
            FlowValidator.KnownVariables(flow));
    }

    [Fact]
    public void TheContextVariablesAreChecked()
    {
        var issue = Single(FlowValidator.Validate(Flow(Agent() with { Context = "Eligió {menu}" }), Ctx()), "variable.never_set");

        Assert.Equal("ai", issue.NodeId);
    }

    private static AiAgentNode Agent() =>
        new(
            "ai", "Asesor IA", Out(("done", "end"), ("human", "end"), ("noAnswer", "end"), ("idle", "end"), ("error", "end")), ActiveAgent,
            "Resuelve dudas.", "El cliente se llama {nombre_contacto}.", "lastMessage", 60, "asesorIa");

    private static FlowDefinition Flow(params FlowNode[] nodes) =>
        new(1, new FlowStart(nodes[0].Id), [], [.. nodes, new HandoffNode("end", "Asesor", Out(), null)]);

    private static FlowValidationContext Ctx(string channel = "WhatsApp", bool aiEnabled = true) =>
        new(
            new HashSet<string> { channel }, new HashSet<Guid>(), new HashSet<Guid>(), true, new HashSet<Guid>(), aiEnabled,
            [new FlowAgentRef(ActiveAgent, "Ventas", "Active"), new FlowAgentRef(DraftAgent, "Nuevo", "Draft"), new FlowAgentRef(PausedAgent, "Viejo", "Paused")]);

    private static FlowIssue Single(IReadOnlyList<FlowIssue> issues, string code) => Assert.Single(issues, issue => issue.Code == code);

    private static Dictionary<string, string?> Out(params (string Key, string? Target)[] outputs) =>
        outputs.ToDictionary(output => output.Key, output => output.Target, StringComparer.Ordinal);
}
