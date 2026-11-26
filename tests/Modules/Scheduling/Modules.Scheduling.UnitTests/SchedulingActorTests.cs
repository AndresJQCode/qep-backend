using Modules.Scheduling.Application;

namespace Modules.Scheduling.UnitTests;

// Spec 2026-11-23 §6.2 y D-17: quién escribe decide el actor del historial, la marca byChannel y el tipo de actor de la auditoría.
public sealed class SchedulingActorTests
{
    [Fact]
    public void AnAgentWritesByChannelWithoutAMember()
    {
        var agentId = Guid.CreateVersion7();

        var actor = SchedulingActor.Agent(agentId);

        Assert.Null(actor.HistoryMemberId);
        Assert.True(actor.ByChannel);
        Assert.True(actor.IsAgent);
        Assert.Equal(agentId, actor.AuditActorId);
    }

    [Fact]
    public void AHumanNeverWritesByChannel()
    {
        var subjectId = Guid.CreateVersion7();

        var human = SchedulingActor.Human(subjectId, null);

        Assert.Null(human.HistoryMemberId);
        Assert.False(human.ByChannel);
        Assert.False(human.IsAgent);
        Assert.Equal(subjectId, human.AuditActorId);
        Assert.Equal(human.HistoryMemberId, SchedulingActor.Human(subjectId, null).HistoryMemberId);
    }

    [Fact]
    public void AHumanKeepsTheMemberForTheHistory()
    {
        var memberId = Guid.CreateVersion7();

        Assert.Equal(memberId, SchedulingActor.Human(Guid.CreateVersion7(), memberId).HistoryMemberId);
    }
}
