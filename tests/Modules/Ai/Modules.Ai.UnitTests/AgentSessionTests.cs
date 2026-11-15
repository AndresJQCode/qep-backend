using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Spec 2026-10-29 §4.2, §4.7, D-A14, D-A16 y §6.3: la sesión, el trabajo de una sesión, el partido de respuestas largas y los ids que
/// se repiten en un reintento.</summary>
public sealed class AgentSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static AgentSession NewSession() =>
        AgentSession.Open(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

    [Fact]
    public void ASessionIsBornLiveAndNumbersItsTurns()
    {
        var session = NewSession();

        var first = session.AppendTurn(AgentTurnRoleKind.Greeting, Guid.CreateVersion7(), null, [], truncated: false, Now);
        var second = session.AppendTurn(AgentTurnRoleKind.Customer, Guid.CreateVersion7(), null, [], truncated: false, Now.AddSeconds(1));

        Assert.True(session.IsLive);
        Assert.Equal((1, 2, 3), (first.Seq, second.Seq, session.NextSeq));
        Assert.Equal(Now.AddSeconds(1), session.UpdatedAt);
    }

    [Fact]
    public void AReplyCountsAndClearsFailuresAndSupersedes()
    {
        var session = NewSession();
        session.RecordFailure(Now);
        session.RecordSuperseded(Now);

        session.RecordReply(Now);

        Assert.Equal((1, 0, 0), (session.RepliesWithoutHuman, session.ConsecutiveFailures, session.SupersededInRow));
    }

    [Fact]
    public void AJobIsSupersededAtMostTwiceInARow()
    {
        var session = NewSession();

        session.RecordSuperseded(Now);
        Assert.True(session.CanSupersede);
        session.RecordSuperseded(Now);

        Assert.False(session.CanSupersede);
    }

    [Fact]
    public void AFinishedSessionTakesNoMoreTurns()
    {
        var session = NewSession();

        session.Finish(AgentSessionStatus.HandedOff, "AgentRequested", "customer_asked", Now);

        Assert.False(session.IsLive);
        Assert.Equal((Now, "AgentRequested", "customer_asked"), (session.EndedAt!.Value, session.EndReason, session.FailureCode));
        Assert.Throws<InvalidOperationException>(() => session.AppendTurn(AgentTurnRoleKind.Customer, null, null, [], false, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewSession().Finish(AgentSessionStatus.Live, null, null, Now));
    }

    [Fact]
    public void APendingSessionJobOnlyMovesForward()
    {
        var job = ModelJob.ForSession(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 3, Now.AddSeconds(2), Now);

        job.ExtendTo(5);
        job.ExtendTo(4);

        Assert.Equal(5, job.UpToSeq);
        Assert.Equal(Now.AddSeconds(2), job.NextAttemptAt);
        Assert.Null(job.TestId);
    }

    // §6.3 paso 4: la respuesta guardada no devuelve el trabajo a Pending, que chocaría con el siguiente pendiente de la sesión (D-A14).
    [Fact]
    public void AHeldResultKeepsTheStatusAndShortensTheLease()
    {
        var job = ModelJob.ForSession(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1, Now, Now);
        var before = job.Status;

        job.HoldResult("{}", Now.AddSeconds(2));

        Assert.Equal(before, job.Status);
        Assert.Equal(Now.AddSeconds(2), job.LeaseUntil);
        Assert.Equal("{}", job.ResultJson);
    }

    [Fact]
    public void AShortReplyIsOnePart()
    {
        var (parts, truncated) = ReplySplitter.Split("  Hola  ", 1000, 3);

        Assert.Equal("Hola", Assert.Single(parts));
        Assert.False(truncated);
    }

    [Fact]
    public void ALongReplySplitsByParagraphs()
    {
        var text = $"{new string('a', 600)}\n\n{new string('b', 600)}";

        var (parts, truncated) = ReplySplitter.Split(text, 1000, 3);

        Assert.Equal([new string('a', 600), new string('b', 600)], parts);
        Assert.False(truncated);
    }

    [Fact]
    public void WhatDoesNotFitInThreePartsIsCut()
    {
        var text = new string('x', 3500);

        var (parts, truncated) = ReplySplitter.Split(text, 1000, 3);

        Assert.Equal(3, parts.Count);
        Assert.All(parts, part => Assert.Equal(1000, part.Length));
        Assert.True(truncated);
    }

    [Fact]
    public void TheIdsOfARetryAreTheSame()
    {
        var message = Guid.CreateVersion7();
        var job = Guid.CreateVersion7();

        Assert.Equal(AiDeterministicIds.SessionFor(message), AiDeterministicIds.SessionFor(message));
        Assert.Equal(AiDeterministicIds.ReplyClientId(job, 0), AiDeterministicIds.ReplyClientId(job, 0));
        Assert.NotEqual(AiDeterministicIds.ReplyClientId(job, 0), AiDeterministicIds.ReplyClientId(job, 1));
        Assert.NotEqual(AiDeterministicIds.ReplyClientId(job, 0), AiDeterministicIds.HandoffClientId(job));
    }
}
