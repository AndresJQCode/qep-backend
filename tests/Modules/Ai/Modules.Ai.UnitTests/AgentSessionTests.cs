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

    // Spec 2026-11-17 §5.4 y D-B5: la sesión del bloque fija la versión publicada al entrar, sabe de qué delegación viene y nace sin turnos
    // (el saludo lo decide el flujo, D-B6).
    [Fact]
    public void OpenForBlockFixesTheVersionAndHasNoGreeting()
    {
        var (version, delegation, chatbot, run) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        var session = AgentSession.OpenForBlock(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), version, delegation,
            chatbot, run, idleMinutes: 45, Now);

        Assert.Equal(AgentSessionOrigin.ChatbotBlock, session.Origin);
        Assert.True(session.IsBlock);
        Assert.Equal((version, (Guid?)delegation, (Guid?)chatbot, (Guid?)run, (int?)45), (session.AgentVersionId, session.DelegationId, session.ChatbotId, session.RunId, session.IdleMinutes));
        Assert.Equal((AgentSessionStatus.Live, 1), (session.Status, session.NextSeq));
        Assert.Equal(AgentSessionOrigin.Connection, NewSession().Origin);
        Assert.Null(NewSession().DelegationId);
    }

    // §6.3: devolver termina la sesión con la salida en end_reason, el motivo en failure_code y el resumen de finish_objective.
    [Fact]
    public void ReturningEndsTheSessionWithTheOutputAndReason()
    {
        var session = AgentSession.OpenForBlock(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), idleMinutes: null, Now);

        session.Return("done", "objective_completed", new string('r', 600), Now.AddMinutes(3));

        Assert.Equal(
            (AgentSessionStatus.Returned, "done", "objective_completed", Now.AddMinutes(3)),
            (session.Status, session.EndReason, session.FailureCode, session.EndedAt!.Value));
        Assert.Equal(AgentSession.ObjectiveSummaryMaxLength, session.ObjectiveSummary!.Length);
        Assert.Throws<InvalidOperationException>(() => session.Return("idle", "idle", null, Now));
        // Una sesión de una conexión no devuelve: no tiene a quién.
        Assert.Throws<InvalidOperationException>(() => NewSession().Return("done", "objective_completed", null, Now));
    }

    [Fact]
    public void TheSessionIdOfADelegationIsDeterministic()
    {
        var delegation = Guid.CreateVersion7();

        Assert.Equal(AiDeterministicIds.SessionForDelegation(delegation), AiDeterministicIds.SessionForDelegation(delegation));
        Assert.NotEqual(AiDeterministicIds.SessionFor(delegation), AiDeterministicIds.SessionForDelegation(delegation));
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
