using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-21 §4.3: lo que el resumen de la conversación y el evento del hilo dicen del asistente.</summary>
public sealed class ConversationAutomationMappingTests
{
    private static readonly DateTimeOffset Started = DateTimeOffset.UnixEpoch.AddHours(1);
    private static readonly DateTimeOffset Paused = DateTimeOffset.UnixEpoch.AddHours(2);
    private static readonly Guid Andres = Guid.CreateVersion7();

    private static ConversationRow Row(
        ConversationAutomationStatus status, DateTimeOffset? startedAt = null, DateTimeOffset? pausedAt = null, Guid? pausedBy = null, AutomationPauseReason? reason = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), UserId: null, WaId: "573001234567", Username: null, "Laura",
            CustomerId: null, AssignedMemberId: null, ConversationStatus.Open, 0, null, null, null, null, null, null, null,
            DateTimeOffset.UnixEpoch, 1,
            AutomationStatus: status, AutomationStartedAt: startedAt, AutomationPausedAt: pausedAt, AutomationPausedByMemberId: pausedBy, AutomationPauseReason: reason);

    private static ConversationSummary Summary(ConversationRow row, IReadOnlyDictionary<Guid, string> memberNames) =>
        ConversationSummaryBuilder.ToSummary(
            row, new Dictionary<Guid, string>(), new Dictionary<Guid, CustomerRefDto>(), new Dictionary<string, CustomerRefDto>(), memberNames, null);

    [Fact]
    public void APausedConversationCarriesWhenWhoAndWhy()
    {
        var names = new Dictionary<Guid, string> { [Andres] = "Andrés" };

        var automation = Summary(Row(ConversationAutomationStatus.Paused, Started, Paused, Andres, AutomationPauseReason.AdvisorTook), names).Automation;

        Assert.NotNull(automation);
        Assert.Equal("Paused", automation.Status);
        Assert.Equal(Started, automation.StartedAt);
        Assert.Equal(Paused, automation.PausedAt);
        Assert.Equal("AdvisorTook", automation.PauseReason);
        Assert.Equal(new MemberRefDto(Andres, "Andrés"), automation.PausedBy);
    }

    [Fact]
    public void APausedByMemberThatIsGoneIsDeletedMember()
    {
        var automation = Summary(Row(ConversationAutomationStatus.Paused, Started, Paused, Andres, AutomationPauseReason.Manual), new Dictionary<Guid, string>()).Automation;

        Assert.Equal(new MemberRefDto(Andres, MessageMapping.DeletedMemberName), automation!.PausedBy);
    }

    [Fact]
    public void AnActiveConversationHasNoPauseData()
    {
        var automation = Summary(Row(ConversationAutomationStatus.Active, Started), new Dictionary<Guid, string>()).Automation;

        Assert.Equal("Active", automation!.Status);
        Assert.Null(automation.PausedBy);
        Assert.Null(automation.PausedAt);
        Assert.Null(automation.PauseReason);
    }

    [Fact]
    public void AConversationWithoutAssistantHasNoAutomation() =>
        Assert.Null(Summary(Row(ConversationAutomationStatus.None), new Dictionary<Guid, string>()).Automation);

    [Theory]
    [InlineData(AutomationPauseReason.AdvisorReplied, "AdvisorReplied")]
    [InlineData(null, null)]
    public void AnAutomationPausedEventCarriesItsReasonByName(AutomationPauseReason? reason, string? expected)
    {
        var details = ConversationEventJson.Serialize(new ConversationEvent(ConversationEventType.AutomationPaused, Actor: Andres, PauseReason: reason));
        var row = new MessageRow(Guid.CreateVersion7(), Guid.CreateVersion7(), MessageDirection.System, MessageKind.Event, null, null, details,
            MessageStatus.Delivered, null, DateTimeOffset.UnixEpoch, null, null, null, null);

        var dto = MessageMapping.ToDto(row, Guid.CreateVersion7(), new Dictionary<Guid, string>(), new Dictionary<Guid, ReplyTargetRow>());

        Assert.Equal(expected, dto.Event!.PauseReason);
    }
}
