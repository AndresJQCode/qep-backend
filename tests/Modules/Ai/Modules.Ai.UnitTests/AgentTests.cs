using BuildingBlocks.Domain;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Spec 2026-10-29 §4.7, D-A5, D-A6 y D-A10; plan 2026-11-12 C-17: el agregado del agente.</summary>
public sealed class AgentTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid By = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);

    private static Agent NewAgent() =>
        Agent.Create(Tenant, "  Asesor virtual  ", AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")), By, Now);

    [Fact]
    public void ItIsBornAsATrimmedDraftAtVersionOne()
    {
        var agent = NewAgent();

        Assert.Equal("Asesor virtual", agent.Name);
        Assert.Equal(AgentStatus.Draft, agent.Status);
        Assert.Equal(1, agent.Version);
        Assert.Null(agent.PublishedVersionId);
        Assert.True(agent.HasDraftChanges);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankNameIsRejected(string name)
    {
        var error = Assert.Throws<AiDomainException>(() =>
            Agent.Create(Tenant, name, AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")), By, Now));

        Assert.Equal(AiErrorCodes.AgentNameInvalid, error.Code);
    }

    [Fact]
    public void PublishingCopiesTheDraftWithItsProviderAndModel()
    {
        var agent = NewAgent();
        agent.SaveDraft(agent.DraftConfig with { Instructions = "Responde dudas." }, By, Now.AddMinutes(1));

        var version = agent.Publish([], By, Now.AddMinutes(2));

        Assert.Equal(AgentStatus.Active, agent.Status);
        Assert.Equal(version.Id, agent.PublishedVersionId);
        Assert.Equal((1, "anthropic", "claude-opus-5-5"), (version.Number, version.Provider, version.Model));
        Assert.Equal(agent.DraftConfigJson, version.ConfigJson);
        Assert.False(agent.HasDraftChanges);
    }

    [Fact]
    public void PublishingWithErrorsIsRejectedWithTheIssuesByPath()
    {
        var agent = NewAgent();

        var error = Assert.Throws<AiDomainException>(() => agent.Publish(
            [new AgentIssue(AgentIssueSeverity.Error, "instructions_missing", "instructions", "Escribe las instrucciones.")], By, Now));

        Assert.Equal(AiErrorCodes.AgentInvalid, error.Code);
        Assert.Equal(["instructions_missing"], ((IHasFieldErrors)error).FieldErrors["instructions"]);
        Assert.Equal(AgentStatus.Draft, agent.Status);
    }

    [Fact]
    public void EnsurePublishableRejectsErrorsWithTheIssuesByPath()
    {
        var error = Assert.Throws<AiDomainException>(() => Agent.EnsurePublishable(
            [new AgentIssue(AgentIssueSeverity.Error, "instructions_missing", "instructions", "Escribe las instrucciones.")]));

        Assert.Equal(AiErrorCodes.AgentInvalid, error.Code);
        Assert.Equal(["instructions_missing"], ((IHasFieldErrors)error).FieldErrors["instructions"]);
    }

    [Fact]
    public void EnsurePublishableLetsWarningsThrough() =>
        Agent.EnsurePublishable([new AgentIssue(AgentIssueSeverity.Warning, "model_unavailable", "model", "…")]);

    [Fact]
    public void WarningsDoNotStopPublishing()
    {
        var agent = NewAgent();

        agent.Publish([new AgentIssue(AgentIssueSeverity.Warning, "model_unavailable", "model", "…")], By, Now);

        Assert.Equal(AgentStatus.Active, agent.Status);
    }

    [Fact]
    public void RepublishingAPausedAgentKeepsItPausedAndNumbersTheVersion()
    {
        var agent = NewAgent();
        agent.Publish([], By, Now);
        agent.Pause(Now);

        var second = agent.Publish([], By, Now.AddMinutes(1));

        Assert.Equal(2, second.Number);
        Assert.Equal(AgentStatus.Paused, agent.Status);
    }

    [Fact]
    public void PauseAndResumeFollowTheStatus()
    {
        var agent = NewAgent();
        Assert.Equal(AiErrorCodes.AgentNotPublished, Assert.Throws<AiDomainException>(() => agent.Pause(Now)).Code);
        Assert.Equal(AiErrorCodes.AgentNotPublished, Assert.Throws<AiDomainException>(() => agent.Resume(Now)).Code);

        agent.Publish([], By, Now);
        Assert.Equal(AiErrorCodes.AgentAlreadyActive, Assert.Throws<AiDomainException>(() => agent.Resume(Now)).Code);
        agent.Pause(Now);
        Assert.Equal(AiErrorCodes.AgentAlreadyPaused, Assert.Throws<AiDomainException>(() => agent.Pause(Now)).Code);
        agent.Resume(Now);

        Assert.Equal(AgentStatus.Active, agent.Status);
    }

    [Fact]
    public void SettingConnectionsReplacesTheSetWithoutRepeats()
    {
        var agent = NewAgent();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        agent.SetConnections([first, second, first], Now);

        agent.SetConnections([second], Now);

        Assert.Equal([second], agent.Connections.Select(connection => connection.ConnectionId));
    }

    [Fact]
    public void EveryChangeMovesTheVersion()
    {
        var agent = NewAgent();

        agent.Rename("Otro nombre", Now);
        agent.SaveDraft(agent.DraftConfig, By, Now);
        agent.SetConnections([Guid.NewGuid()], Now);

        Assert.Equal(4, agent.Version);
    }
}
