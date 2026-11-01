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

    private static AutomationConversationState PausedByHand(DateTimeOffset? resumedAt = null, string reason = "Manual") =>
        State(automation: "Paused", resumedAt: resumedAt) with { AutomationPauseReason = reason, AutomationPausedAt = StartedAt.AddMinutes(30) };

    /// <summary>Spec 2026-11-01 D-4: pausa manual, corrida esperando y nadie la devolvió después de que arrancó.</summary>
    [Fact]
    public void AManualPauseWithAWaitingRunCanContinue() =>
        Assert.True(BotAttendance.CanContinue(PausedByHand(), Run()));

    [Theory]
    [InlineData(-5, true)]
    [InlineData(0, true)]
    [InlineData(5, false)]
    public void AResumeAfterTheRunStartedForbidsContinuing(int resumedOffsetSeconds, bool expected) =>
        Assert.Equal(expected, BotAttendance.CanContinue(PausedByHand(StartedAt.AddSeconds(resumedOffsetSeconds)), Run()));

    /// <summary>D-4.2: <c>Handoff</c> no tiene corrida viva, y los motivos automáticos son una persona que ya intervino.</summary>
    [Theory]
    [InlineData("Handoff")]
    [InlineData("AdvisorTook")]
    [InlineData("AdvisorReplied")]
    [InlineData("AdvisorTransferred")]
    public void AnyOtherPauseReasonCannotContinue(string reason) =>
        Assert.False(BotAttendance.CanContinue(PausedByHand(reason: reason), Run()));

    [Theory]
    [InlineData("Active")]
    [InlineData("None")]
    public void AnAssistantThatIsNotPausedCannotContinue(string automation) =>
        Assert.False(BotAttendance.CanContinue(PausedByHand() with { AutomationStatus = automation }, Run()));

    /// <summary>D-4.3: una <c>Running</c> con el candado libre es una unidad muerta.</summary>
    [Fact]
    public void ARunningRunCannotContinue() =>
        Assert.False(BotAttendance.CanContinue(PausedByHand(), Run(RunStatus.Running)));

    [Fact]
    public void WithoutARunThereIsNothingToContinue() =>
        Assert.False(BotAttendance.CanContinue(PausedByHand(), null));

    [Fact]
    public void AFinishedRunCannotContinue()
    {
        var run = Run();
        run.Finish(RunStatus.Interrupted, null, StartedAt.AddMinutes(1));

        Assert.False(BotAttendance.CanContinue(PausedByHand(), run));
    }
}
