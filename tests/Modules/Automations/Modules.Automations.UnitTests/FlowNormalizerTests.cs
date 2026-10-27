using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

public sealed class FlowNormalizerTests
{
    private static readonly Guid Ana = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // Spec 2026-10-25 D-3/D-4: con repeat o handoff el canvas no dibuja «otra respuesta»; una conexión vieja no debe sobrevivir.
    [Theory]
    [InlineData("repeat", false)]
    [InlineData("handoff", false)]
    [InlineData("otherOutput", true)]
    public void AHiddenOtherRouteIsDropped(string fallback, bool kept)
    {
        var outputs = new Dictionary<string, string?> { ["a"] = "n2", ["other"] = "n3" };
        var flow = Flow(
            new ButtonsNode("b1", "B", outputs, "t", [new FlowButton("a", "A")], null, fallback),
            new ListNode("l1", "L", outputs, "t", "Ver", new ListOptions("fixed", [], null, null, null, null, null), "x", fallback));

        var result = FlowNormalizer.Normalize(flow);

        Assert.All(result.Nodes, node =>
        {
            Assert.Equal(kept, node.Outputs.ContainsKey("other"));
            Assert.Equal("n2", node.Outputs["a"]);
        });
    }

    [Fact]
    public void ADataConsentIsUntouched()
    {
        var outputs = new Dictionary<string, string?> { ["accepted"] = "n2", ["rejected"] = "n3", ["other"] = "n4" };
        var consent = new DataConsentNode("d1", "Política", outputs, "t");

        var result = FlowNormalizer.Normalize(Flow(consent));

        var kept = Assert.IsType<DataConsentNode>(Assert.Single(result.Nodes));
        Assert.Equal(consent.Text, kept.Text);
        Assert.Equal(outputs, kept.Outputs);
    }

    // Spec 2026-10-26 repetir R-1, R-2: «rejected» sólo existe con rejectedOutput y el miembro sólo con repeat o handoff.
    [Theory]
    [InlineData("repeat", false)]
    [InlineData("handoff", false)]
    [InlineData("rejectedOutput", true)]
    public void ADataConsentWithoutTheRejectedOutputDropsIt(string onReject, bool kept)
    {
        var outputs = new Dictionary<string, string?> { ["accepted"] = "n2", ["rejected"] = "n9" };

        var result = FlowNormalizer.Normalize(Flow(new DataConsentNode("d1", "Política", outputs, "t", onReject)));

        var consent = Assert.IsType<DataConsentNode>(Assert.Single(result.Nodes));
        Assert.Equal(kept, consent.Outputs.ContainsKey("rejected"));
        Assert.Equal("n2", consent.Outputs["accepted"]);
    }

    [Theory]
    [InlineData("repeat", true)]
    [InlineData("handoff", true)]
    [InlineData("rejectedOutput", false)]
    public void TheRejectMemberOnlyStaysWhenItHandsOff(string onReject, bool kept)
    {
        var consent = new DataConsentNode("d1", "Política", new Dictionary<string, string?>(), "t", onReject, 4, Ana);

        var result = Assert.IsType<DataConsentNode>(Assert.Single(FlowNormalizer.Normalize(Flow(consent)).Nodes));

        Assert.Equal(kept ? Ana : (Guid?)null, result.OnRejectMemberId);
        Assert.Equal(4, result.MaxAsks);
    }

    [Theory]
    [InlineData("repeat", false)]
    [InlineData("otherOutput", false)]
    [InlineData("handoff", true)]
    public void AFallbackMemberOutsideHandoffIsDropped(string fallback, bool kept)
    {
        var flow = Flow(
            new ButtonsNode("b1", "B", new Dictionary<string, string?>(), "t", [], null, fallback, Ana),
            new ListNode("l1", "L", new Dictionary<string, string?>(), "t", "Ver", new ListOptions("fixed", [], null, null, null, null, null), "x", fallback, Ana));

        var result = FlowNormalizer.Normalize(flow);

        Assert.Equal(kept ? Ana : (Guid?)null, Assert.IsType<ButtonsNode>(result.Nodes[0]).FallbackMemberId);
        Assert.Equal(kept ? Ana : (Guid?)null, Assert.IsType<ListNode>(result.Nodes[1]).FallbackMemberId);
    }

    [Fact]
    public void TheOtherNodesAreUntouched()
    {
        var outputs = new Dictionary<string, string?> { ["next"] = "n2" };
        var flow = Flow(
            new MessageNode("m1", "M", outputs, "hola", null),
            new OpenQuestionNode("q1", "Q", new Dictionary<string, string?> { ["answered"] = "n2" }, "¿?", "x", new QuestionValidation("none", null, null), null),
            new HandoffNode("h1", "H", new Dictionary<string, string?>(), "chao", Ana));

        var result = FlowNormalizer.Normalize(flow);

        Assert.Equal(flow.Nodes, result.Nodes);
        Assert.Equal(Ana, Assert.IsType<HandoffNode>(result.Nodes[2]).MemberId);
    }

    private static FlowDefinition Flow(params FlowNode[] nodes) => new(1, new FlowStart(null), [], nodes);
}
