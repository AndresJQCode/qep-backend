using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-10-31 D-7: la regla compartida de «el bot de verdad está atendiendo».</summary>
public sealed class BotAttendanceTests
{
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static ConversationRun Run(RunStatus status = RunStatus.Waiting) =>
        ConversationRun.Start(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), ConnectionId, Guid.CreateVersion7(), Guid.CreateVersion7(),
            RunState.Initial(RunVariables.Empty()) with { Status = status }, StartedAt);

    private static AutomationConversationState State(
        string status = "Open", Guid? member = null, string automation = "Active", DateTimeOffset? resumedAt = null) =>
        new(status, member, automation, Guid.CreateVersion7(), ConnectionId, StartedAt, "Laura", "573001234567", resumedAt);

    [Fact]
    public void WithoutALiveRunNobodyIsAttending() =>
        Assert.False(BotAttendance.IsAttending(State(), null));

    [Fact]
    public void AFinishedRunIsNotAttending()
    {
        var run = Run();
        run.Finish(RunStatus.Completed, null, StartedAt.AddMinutes(1));

        Assert.False(BotAttendance.IsAttending(State(), run));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(-5, true)]
    [InlineData(0, true)]
    [InlineData(5, false)]
    public void AResumeAfterTheRunStartedMeansAPersonTookOver(int? resumedOffsetSeconds, bool attending)
    {
        var resumedAt = resumedOffsetSeconds is { } seconds ? StartedAt.AddSeconds(seconds) : (DateTimeOffset?)null;

        Assert.Equal(attending, BotAttendance.IsAttending(State(resumedAt: resumedAt), Run()));
    }

    [Fact]
    public void AnActiveBotWithoutAdvisorIsAttending() =>
        Assert.True(BotAttendance.IsAttending(State(), Run()));

    [Fact]
    public void AnAdvisorTakesTheBotOut() =>
        Assert.False(BotAttendance.IsAttending(State(member: Guid.CreateVersion7()), Run()));

    [Fact]
    public void AResolvedConversationIsNotAttended() =>
        Assert.False(BotAttendance.IsAttending(State(status: "Resolved"), Run()));

    [Theory]
    [InlineData("Paused")]
    [InlineData("None")]
    public void APausedOrOffAssistantIsNotAttending(string automation) =>
        Assert.False(BotAttendance.IsAttending(State(automation: automation), Run()));
}
