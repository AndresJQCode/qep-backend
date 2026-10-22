using Modules.Automations.Domain;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

public sealed class ChatbotTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Member = Guid.NewGuid();
    private static readonly IReadOnlyList<FlowIssue> NoIssues = [];

    private static Chatbot NewChatbot() =>
        Chatbot.Create(Tenant, "  Soporte  ", "Atiende el soporte", ChatbotTrigger.NewConversation, [], "{\"a\":1}", "{\"l\":1}", Member, T0);

    [Fact]
    public void CreateStartsAsDraftWithVersionOne()
    {
        var chatbot = NewChatbot();

        Assert.Equal("Soporte", chatbot.Name);
        Assert.Equal(ChatbotStatus.Draft, chatbot.Status);
        Assert.Equal(1, chatbot.Version);
        Assert.Null(chatbot.PublishedVersionId);
        Assert.Equal(Chatbot.DefaultMaxAttempts, chatbot.MaxAttempts);
        Assert.Equal(Chatbot.DefaultRestartKeyword, chatbot.RestartKeyword);
        Assert.True(chatbot.HasDraftChanges);
        Assert.Equal(T0, chatbot.DraftUpdatedAt);
        foreach (var name in new[] { "  ", new string('x', 81) })
        {
            var error = Assert.Throws<AutomationsDomainException>(() =>
                Chatbot.Create(Tenant, name, "", ChatbotTrigger.NewConversation, [], "{}", "{}", Member, T0));
            Assert.Equal(AutomationsErrorCodes.ChatbotNameInvalid, error.Code);
        }
    }

    [Fact]
    public void PublishWithAnErrorIssueThrowsChatbotInvalid()
    {
        var chatbot = NewChatbot();
        FlowIssue[] issues =
        [
            new(FlowIssueSeverity.Warning, "x", null, "aviso"),
            new(FlowIssueSeverity.Error, "y", "n1", "error"),
        ];

        var error = Assert.Throws<AutomationsDomainException>(() => chatbot.Publish(issues, Member, T0.AddMinutes(1)));

        Assert.Equal(AutomationsErrorCodes.ChatbotInvalid, error.Code);
        Assert.Equal(ChatbotStatus.Draft, chatbot.Status);
        Assert.Empty(chatbot.Versions);
    }

    [Fact]
    public void PublishCreatesVersionOneAndActivates()
    {
        var chatbot = NewChatbot();
        chatbot.SetConnections([Guid.NewGuid(), Guid.NewGuid()], T0);
        var now = T0.AddMinutes(5);

        var version = chatbot.Publish([new(FlowIssueSeverity.Warning, "x", null, "aviso")], Member, now);

        Assert.Equal(1, version.Number);
        Assert.Equal("{\"a\":1}", version.FlowJson);
        Assert.Equal("{\"l\":1}", version.LayoutJson);
        Assert.Equal(now, version.PublishedAt);
        Assert.Equal(ChatbotStatus.Active, chatbot.Status);
        Assert.Equal(version.Id, chatbot.PublishedVersionId);
        Assert.All(chatbot.Connections, connection => Assert.True(connection.Active));
        Assert.False(chatbot.HasDraftChanges);
    }

    [Fact]
    public void PublishTwiceNumbersSequentially()
    {
        var chatbot = NewChatbot();

        var first = chatbot.Publish(NoIssues, Member, T0.AddMinutes(1));
        var second = chatbot.Publish(NoIssues, Member, T0.AddMinutes(2));

        Assert.Equal([1, 2], new[] { first.Number, second.Number });
        Assert.Equal(second.Id, chatbot.PublishedVersionId);
        Assert.Equal(2, chatbot.Versions.Count);
    }

    [Fact]
    public void SaveDraftAfterPublishMarksDraftChanges()
    {
        var chatbot = NewChatbot();
        chatbot.Publish(NoIssues, Member, T0.AddMinutes(1));
        Assert.False(chatbot.HasDraftChanges);

        chatbot.SaveDraft("{\"a\":2}", "{\"l\":2}", Member, T0.AddMinutes(2));

        Assert.True(chatbot.HasDraftChanges);
        Assert.Equal("{\"a\":2}", chatbot.DraftFlowJson);
        Assert.Equal("{\"a\":1}", chatbot.Versions.Single().FlowJson);
    }

    [Fact]
    public void PauseRequiresActiveAndResumeRequiresPaused()
    {
        var chatbot = NewChatbot();
        Assert.Equal(AutomationsErrorCodes.ChatbotNotPublished,
            Assert.Throws<AutomationsDomainException>(() => chatbot.Pause(T0)).Code);
        Assert.Equal(AutomationsErrorCodes.ChatbotNotPublished,
            Assert.Throws<AutomationsDomainException>(() => chatbot.Resume(T0)).Code);

        chatbot.Publish(NoIssues, Member, T0.AddMinutes(1));
        Assert.Equal(AutomationsErrorCodes.ChatbotAlreadyActive,
            Assert.Throws<AutomationsDomainException>(() => chatbot.Resume(T0)).Code);

        chatbot.Pause(T0.AddMinutes(2));
        Assert.Equal(ChatbotStatus.Paused, chatbot.Status);
        Assert.Equal(AutomationsErrorCodes.ChatbotAlreadyPaused,
            Assert.Throws<AutomationsDomainException>(() => chatbot.Pause(T0)).Code);

        chatbot.Resume(T0.AddMinutes(3));
        Assert.Equal(ChatbotStatus.Active, chatbot.Status);
    }

    [Fact]
    public void PublishWhilePausedStaysPaused()
    {
        var chatbot = NewChatbot();
        chatbot.SetConnections([Guid.NewGuid()], T0);
        chatbot.Publish(NoIssues, Member, T0.AddMinutes(1));
        chatbot.Pause(T0.AddMinutes(2));

        var version = chatbot.Publish(NoIssues, Member, T0.AddMinutes(3));

        Assert.Equal(2, version.Number);
        Assert.Equal(ChatbotStatus.Paused, chatbot.Status);
        Assert.All(chatbot.Connections, connection => Assert.True(connection.Active));
    }

    [Fact]
    public void SetConnectionsOnADraftLeavesThemInactive()
    {
        var chatbot = NewChatbot();
        var kept = Guid.NewGuid();
        chatbot.SetConnections([kept, Guid.NewGuid(), kept], T0);

        Assert.Equal(2, chatbot.Connections.Count);
        Assert.All(chatbot.Connections, connection => Assert.False(connection.Active));

        chatbot.SetConnections([kept], T0.AddMinutes(1));
        Assert.Equal([kept], chatbot.Connections.Select(connection => connection.ConnectionId));

        chatbot.Publish(NoIssues, Member, T0.AddMinutes(2));
        chatbot.SetConnections([kept, Guid.NewGuid()], T0.AddMinutes(3));
        Assert.All(chatbot.Connections, connection => Assert.True(connection.Active));
    }

    [Fact]
    public void KeywordsAreNormalizedAndCapped()
    {
        var chatbot = Chatbot.Create(Tenant, "Bot", "", ChatbotTrigger.Keyword, ["Reiniciar", "reiniciar", " hola "], "{}", "{}", Member, T0);
        Assert.Equal(["reiniciar", "hola"], chatbot.Keywords);

        chatbot.UpdateSettings(ChatbotTrigger.Keyword, ["Cotización", "COTIZACION"], " Reiniciar ", 4, T0);
        Assert.Equal(["cotizacion"], chatbot.Keywords);
        Assert.Equal("reiniciar", chatbot.RestartKeyword);
        Assert.Equal(4, chatbot.MaxAttempts);

        var eleven = Enumerable.Range(1, 11).Select(index => $"palabra{index}").ToList();
        Assert.Equal(AutomationsErrorCodes.ChatbotKeywordsTooMany,
            Assert.Throws<AutomationsDomainException>(() => chatbot.UpdateSettings(ChatbotTrigger.Keyword, eleven, "reiniciar", 3, T0)).Code);
        Assert.Equal(AutomationsErrorCodes.ChatbotKeywordTooLong,
            Assert.Throws<AutomationsDomainException>(() => chatbot.UpdateSettings(ChatbotTrigger.Keyword, [new string('a', 41)], "reiniciar", 3, T0)).Code);
        Assert.Equal(["cotizacion"], chatbot.Keywords);
    }

    [Fact]
    public void EveryMutationBumpsVersion()
    {
        var chatbot = NewChatbot();
        var expected = 1L;
        void Check(Action mutate)
        {
            mutate();
            Assert.Equal(++expected, chatbot.Version);
        }

        Check(() => chatbot.Rename("Otro", "desc", T0.AddMinutes(1)));
        Check(() => chatbot.UpdateSettings(ChatbotTrigger.Keyword, ["hola"], "reiniciar", 3, T0.AddMinutes(2)));
        Check(() => chatbot.SaveDraft("{}", "{}", Member, T0.AddMinutes(3)));
        Check(() => chatbot.SetConnections([Guid.NewGuid()], T0.AddMinutes(4)));
        Check(() => chatbot.Publish(NoIssues, Member, T0.AddMinutes(5)));
        Check(() => chatbot.Pause(T0.AddMinutes(6)));
        Check(() => chatbot.Resume(T0.AddMinutes(7)));
        Assert.Equal(T0.AddMinutes(7), chatbot.UpdatedAt);
    }
}
