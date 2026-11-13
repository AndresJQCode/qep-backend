using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Spec 2026-10-29 §4.6 y D-A7, plan 2026-11-12 C-16: guardar siempre guarda y devuelve los problemas; publicar falla con los
/// errores. El modelo es <c>{ provider, id }</c>: fuera de la lista de su proveedor es aviso al guardar y error al publicar.</summary>
public sealed class AgentConfigValidatorTests
{
    private static AgentConfig Valid() =>
        AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")) with { Instructions = "Atiende a los clientes de la tienda." };

    private static AgentValidationContext Context(
        int connections = 1, AgentModelAvailability model = AgentModelAvailability.Available, bool? memberCanReply = null,
        bool publishing = false) =>
        new(connections, model, memberCanReply, publishing);

    [Fact]
    public void AValidConfigHasNoIssues() =>
        Assert.Empty(AgentConfigValidator.Validate(Valid(), Context()));

    [Fact]
    public void TheDefaultConfigOnlyMissesInstructionsAndConnections()
    {
        var issues = AgentConfigValidator.Validate(
            AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")), Context(connections: 0));

        Assert.Equal(
            [("instructions_missing", "instructions", AgentIssueSeverity.Error), ("connections_missing", "connections", AgentIssueSeverity.Error)],
            issues.Select(issue => (issue.Code, issue.Path, issue.Severity)));
    }

    [Fact]
    public void BlankInstructionsAreAnError()
    {
        var issue = Assert.Single(AgentConfigValidator.Validate(Valid() with { Instructions = "   " }, Context()));

        Assert.Equal(("instructions_missing", "instructions", AgentIssueSeverity.Error), (issue.Code, issue.Path, issue.Severity));
    }

    [Fact]
    public void AModelOutsideItsProviderListIsAWarningWhenSaving()
    {
        var issue = Assert.Single(AgentConfigValidator.Validate(Valid(), Context(model: AgentModelAvailability.Unavailable)));

        Assert.Equal(("model_unavailable", "model", AgentIssueSeverity.Warning), (issue.Code, issue.Path, issue.Severity));
    }

    [Fact]
    public void AModelOutsideItsProviderListIsAnErrorWhenPublishing()
    {
        var issue = Assert.Single(AgentConfigValidator.Validate(
            Valid(), Context(model: AgentModelAvailability.Unavailable, publishing: true)));

        Assert.Equal(("model_unavailable", "model", AgentIssueSeverity.Error), (issue.Code, issue.Path, issue.Severity));
    }

    [Fact]
    public void AnUnknownProviderIsAlwaysAnError()
    {
        var issue = Assert.Single(AgentConfigValidator.Validate(
            Valid() with { Model = new AgentModelRef("gemini", "x") }, Context(model: AgentModelAvailability.UnknownProvider)));

        Assert.Equal(("model_provider_unknown", "model.provider", AgentIssueSeverity.Error), (issue.Code, issue.Path, issue.Severity));
    }

    [Fact]
    public void AHandoffToAMemberNeedsTheMember()
    {
        var config = Valid() with { Handoff = Valid().Handoff with { Destination = AgentHandoff.ToMember, MemberId = null } };

        var issue = Assert.Single(AgentConfigValidator.Validate(config, Context()));

        Assert.Equal(("handoff_member_missing", "handoff.memberId", AgentIssueSeverity.Error), (issue.Code, issue.Path, issue.Severity));
    }

    [Fact]
    public void AHandoffToAMemberWhoCannotReplyIsAnError()
    {
        var config = Valid() with { Handoff = Valid().Handoff with { Destination = AgentHandoff.ToMember, MemberId = Guid.NewGuid() } };

        var issue = Assert.Single(AgentConfigValidator.Validate(config, Context(memberCanReply: false)));

        Assert.Equal(("handoff_member_cannot_reply", "handoff.memberId", AgentIssueSeverity.Error), (issue.Code, issue.Path, issue.Severity));
    }

    [Fact]
    public void AHandoffToAMemberWhoCanReplyIsValid()
    {
        var config = Valid() with { Handoff = Valid().Handoff with { Destination = AgentHandoff.ToMember, MemberId = Guid.NewGuid() } };

        Assert.Empty(AgentConfigValidator.Validate(config, Context(memberCanReply: true)));
    }

    [Fact]
    public void TheConfigSurvivesItsJsonRoundTrip()
    {
        var config = Valid() with
        {
            Greeting = "Hola, soy el asistente.",
            Faq = [new AgentFaq("envios", "¿Hacen envíos?", "Sí, a todo el país.")],
            Tools = new AgentTools(Catalog: true, OrderStatus: false, QuotationDraft: true),
        };

        var read = AgentConfig.FromJson(config.ToJson());

        Assert.Equal(config.ToJson(), read.ToJson());
        Assert.Contains("\"model\":{\"provider\":\"anthropic\",\"id\":\"claude-opus-5-5\"}", config.ToJson(), StringComparison.Ordinal);
    }
}
