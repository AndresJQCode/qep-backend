using System.Text.Json.Nodes;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-11-19 (chatbot CRM action): the <c>crmAction</c> block in the flow document, the normalizer and the validator.</summary>
public sealed class FlowCrmActionTests
{
    private static readonly Guid Tag = Guid.Parse("7a7a7a7a-7a7a-7a7a-7a7a-7a7a7a7a7a7a");
    private static readonly Guid OpenStage = Guid.Parse("5e5e5e5e-5e5e-5e5e-5e5e-5e5e5e5e5e5e");

    [Theory]
    [InlineData("addTag", "7a7a7a7a-7a7a-7a7a-7a7a-7a7a7a7a7a7a", null)]
    [InlineData("removeTag", "7a7a7a7a-7a7a-7a7a-7a7a-7a7a7a7a7a7a", null)]
    [InlineData("moveDeal", null, "5e5e5e5e-5e5e-5e5e-5e5e-5e5e5e5e5e5e")]
    public void RoundTripsEveryOperation(string operation, string? tagId, string? stageId)
    {
        var json = Json(operation, tagId, stageId);

        var flow = FlowJson.Parse(json);

        var node = Assert.IsType<CrmActionNode>(flow.Nodes[0]);
        Assert.Equal(FlowNodeKinds.CrmAction, node.Kind);
        Assert.Equal(
            (operation, tagId is null ? (Guid?)null : Guid.Parse(tagId), stageId is null ? (Guid?)null : Guid.Parse(stageId), "crm"),
            (node.Operation, node.TagId, node.StageId, node.SaveTo));
        Assert.Equal(["ok", "skipped", "error"], FlowNodeKinds.OutputsOf(node));
        Assert.Equal("end", node.Outputs["ok"]);
        Assert.Null(node.Outputs["skipped"]);

        var serialized = FlowJson.Serialize(flow);
        Assert.Equal(serialized, FlowJson.Serialize(FlowJson.Parse(serialized)));
        var written = Assert.IsType<JsonObject>(JsonNode.Parse(serialized)!["nodes"]![0]);
        Assert.Equal(operation, written["operation"]!.GetValue<string>());
        Assert.Equal(tagId, written["tagId"]?.GetValue<string>());
        Assert.Equal(stageId, written["stageId"]?.GetValue<string>());
        Assert.Equal("crm", written["saveTo"]!.GetValue<string>());
        Assert.True(written.ContainsKey("tagId") && written.ContainsKey("stageId"));
    }

    [Fact]
    public void AnUnknownOperationIsMalformed()
    {
        var exception = Assert.Throws<FlowFormatException>(() => FlowJson.Parse(Json("updateField", null, null)));

        Assert.Equal("flow.malformed", exception.Code);
    }

    [Fact]
    public void NormalizerClearsTheFieldsOfOtherOperations()
    {
        var addTag = Node("addTag") with { StageId = OpenStage };
        var moveDeal = Node("moveDeal") with { TagId = Tag };

        var normalized = FlowNormalizer.Normalize(Flow(addTag, moveDeal with { Id = "crm-2" }));

        var nodes = JsonNode.Parse(FlowJson.Serialize(normalized))!["nodes"]!.AsArray();
        Assert.Null(nodes[0]!["stageId"]);
        Assert.Equal(Tag.ToString("D"), nodes[0]!["tagId"]!.GetValue<string>());
        Assert.Null(nodes[1]!["tagId"]);
        Assert.Equal(OpenStage.ToString("D"), nodes[1]!["stageId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("addTag")]
    [InlineData("removeTag")]
    public void TagMissingWhenEmptyOrNotInCatalog(string operation)
    {
        var empty = FlowValidator.Validate(Flow(Node(operation) with { TagId = null }), Ctx());
        var foreign = FlowValidator.Validate(Flow(Node(operation) with { TagId = Guid.NewGuid() }), Ctx());

        Assert.Equal(FlowIssueSeverity.Error, Single(empty, "crm.tag_missing").Severity);
        Assert.Equal(FlowIssueSeverity.Error, Single(foreign, "crm.tag_missing").Severity);
        Assert.DoesNotContain(FlowValidator.Validate(Flow(Node(operation)), Ctx()), issue => issue.Code.StartsWith("crm.", StringComparison.Ordinal));
    }

    [Fact]
    public void StageMissingWhenEmptyNotOpenOrUnknown()
    {
        var closedStage = Guid.NewGuid();

        foreach (var stageId in new Guid?[] { null, closedStage, Guid.NewGuid() })
        {
            var issues = FlowValidator.Validate(Flow(Node("moveDeal") with { StageId = stageId }), Ctx());

            Assert.Equal(FlowIssueSeverity.Error, Single(issues, "crm.stage_missing").Severity);
        }

        Assert.DoesNotContain(FlowValidator.Validate(Flow(Node("moveDeal")), Ctx()), issue => issue.Code.StartsWith("crm.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("addTag")]
    [InlineData("removeTag")]
    public void ModuleOffWhenCustomersOffForTagOps(string operation)
    {
        var issues = FlowValidator.Validate(Flow(Node(operation) with { TagId = Guid.NewGuid() }), Ctx(customers: false));

        Assert.Equal(FlowIssueSeverity.Error, Single(issues, "crm.module_off").Severity);
        Assert.DoesNotContain(issues, issue => issue.Code == "crm.tag_missing");
        Assert.DoesNotContain(FlowValidator.Validate(Flow(Node(operation)), Ctx(pipeline: false)), issue => issue.Code == "crm.module_off");
    }

    [Fact]
    public void ModuleOffWhenPipelineOffForMoveDeal()
    {
        var issues = FlowValidator.Validate(Flow(Node("moveDeal") with { StageId = null }), Ctx(pipeline: false));

        Assert.Equal(FlowIssueSeverity.Error, Single(issues, "crm.module_off").Severity);
        Assert.DoesNotContain(issues, issue => issue.Code == "crm.stage_missing");
        Assert.DoesNotContain(FlowValidator.Validate(Flow(Node("moveDeal")), Ctx(customers: false)), issue => issue.Code == "crm.module_off");
    }

    // A context without the CRM fields (every caller before this block) is the modules off and empty catalogs.
    [Fact]
    public void ADefaultContextIsModulesOff()
    {
        var context = new FlowValidationContext(
            new HashSet<string> { "WhatsApp" }, new HashSet<Guid>(), new HashSet<Guid>(), true, new HashSet<Guid>());

        Assert.Equal(FlowIssueSeverity.Error, Single(FlowValidator.Validate(Flow(Node("addTag")), context), "crm.module_off").Severity);
    }

    [Fact]
    public void SaveToIsWrittenVariable()
    {
        var message = new MessageNode("m1", "Resultado", Out(("next", null)), "Resultado: {crm.result}", null);
        var flow = Flow(Node("addTag") with { Outputs = Out(("ok", "m1"), ("skipped", "end"), ("error", "end")) }, message);

        Assert.DoesNotContain(FlowValidator.Validate(flow, Ctx()), issue => issue.Code == "variable.never_set");
        Assert.Contains("crm.result", FlowValidator.KnownVariables(flow));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Resultado CRM")]
    public void InvalidSaveToNameIsAnError(string saveTo)
    {
        var issue = Single(FlowValidator.Validate(Flow(Node("addTag") with { SaveTo = saveTo }), Ctx()), "variable.name_invalid");

        Assert.Equal((FlowIssueSeverity.Error, "crm"), (issue.Severity, issue.NodeId));
    }

    [Fact]
    public void UnconnectedSkippedAndErrorAreWarningsNotErrors()
    {
        var issues = FlowValidator.Validate(Flow(Node("addTag") with { Outputs = Out(("ok", "end"), ("skipped", null), ("error", null)) }), Ctx());

        Assert.DoesNotContain(issues, issue => issue.Severity == FlowIssueSeverity.Error);
        var unconnected = issues.Where(issue => issue.Code == "node.output_unconnected_optional").ToList();
        Assert.Equal(2, unconnected.Count);
        Assert.Contains(unconnected, issue => issue.Message == "La salida «Sin nada que hacer» de «Etiquetar» no está conectada.");

        var okUnconnected = FlowValidator.Validate(Flow(Node("addTag") with { Outputs = Out(("ok", null), ("skipped", "end"), ("error", "end")) }), Ctx());
        Assert.Equal(FlowIssueSeverity.Error, Single(okUnconnected, "node.output_unconnected").Severity);
    }

    [Fact]
    public void ResultCodesMapToTheirOutputs()
    {
        foreach (var code in new[] { "added", "already_tagged", "removed", "not_tagged", "moved", "already_in_stage" })
        {
            Assert.Equal("ok", CrmResultCodes.OutputOf(code));
        }

        foreach (var code in new[] { "no_open_deal", "several_open_deals", "no_customer" })
        {
            Assert.Equal("skipped", CrmResultCodes.OutputOf(code));
        }

        foreach (var code in new[] { "customer_unavailable", "tag_limit", "deal_conflict", "stage_not_open", "module_off", "tag_missing", "stage_missing", "failed", "weird" })
        {
            Assert.Equal("error", CrmResultCodes.OutputOf(code));
        }

        Assert.Equal(
            ["added", "removed", "moved"],
            new[] { CrmOperations.AddTag, CrmOperations.RemoveTag, CrmOperations.MoveDeal }.Select(CrmResultCodes.SuccessCodeOf));
    }

    private static string Json(string operation, string? tagId, string? stageId) => $$"""
        {
          "schema": 1,
          "start": { "firstNodeId": "crm" },
          "variables": [],
          "nodes": [
            { "id": "crm", "kind": "crmAction", "title": "Etiquetar", "operation": "{{operation}}",
              "tagId": {{Quote(tagId)}}, "stageId": {{Quote(stageId)}}, "saveTo": "crm",
              "outputs": { "ok": "end", "skipped": null, "error": "end" } },
            { "id": "end", "kind": "handoff", "title": "Asesor", "text": null, "memberId": null, "outputs": {} }
          ]
        }
        """;

    private static string Quote(string? value) => value is null ? "null" : $"\"{value}\"";

    private static CrmActionNode Node(string operation) =>
        new(
            "crm", "Etiquetar", Out(("ok", "end"), ("skipped", "end"), ("error", "end")), operation,
            operation == CrmOperations.MoveDeal ? null : Tag, operation == CrmOperations.MoveDeal ? OpenStage : null, "crm");

    private static FlowDefinition Flow(params FlowNode[] nodes) =>
        new(1, new FlowStart(nodes[0].Id), [], [.. nodes, new HandoffNode("end", "Asesor", Out(), null)]);

    private static FlowValidationContext Ctx(bool customers = true, bool pipeline = true) =>
        new(
            new HashSet<string> { "WhatsApp" }, new HashSet<Guid>(), new HashSet<Guid>(), true, new HashSet<Guid>(),
            CustomersEnabled: customers, PipelineEnabled: pipeline, CrmTagIds: new HashSet<Guid> { Tag },
            CrmOpenStageIds: new HashSet<Guid> { OpenStage });

    private static FlowIssue Single(IReadOnlyList<FlowIssue> issues, string code) => Assert.Single(issues, issue => issue.Code == code);

    private static Dictionary<string, string?> Out(params (string Key, string? Target)[] outputs) =>
        outputs.ToDictionary(output => output.Key, output => output.Target, StringComparer.Ordinal);
}
