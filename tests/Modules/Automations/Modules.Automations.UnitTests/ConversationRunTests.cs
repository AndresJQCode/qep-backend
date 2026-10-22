using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-10-21 §4.2 y D-M12: la entidad guarda el <see cref="RunState"/> entre dos pasos del motor y lo devuelve igual.</summary>
public sealed class ConversationRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 15, 0, 0, TimeSpan.Zero);

    private static ConversationRun NewRun(RunState state) =>
        ConversationRun.Start(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), state, Now);

    [Fact]
    public void StartIsRunningWithTheInitialVariables()
    {
        var id = Guid.CreateVersion7();
        var variables = RunVariables.Parse("""{"nombre":"Ana"}""");

        var run = ConversationRun.Start(
            id, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            RunState.Initial(variables), Now);

        Assert.Equal(id, run.Id);
        Assert.Equal(RunStatus.Running, run.Status);
        Assert.True(run.IsLive);
        Assert.Equal(Now, run.StartedAt);
        Assert.Equal(Now, run.UpdatedAt);
        Assert.Null(run.EndedAt);
        Assert.Equal("Ana", RunVariables.Parse(run.VariablesJson).TextOf("nombre"));
        Assert.Equal("[]", run.TraceJson);
    }

    [Fact]
    public void ApplyATerminalStateSetsEndedAt()
    {
        var run = NewRun(RunState.Initial(RunVariables.Empty()));
        var later = Now.AddMinutes(3);

        run.Apply(new RunState(RunStatus.Completed, "end", 4, 0, RunVariables.Empty(), []), later);

        Assert.False(run.IsLive);
        Assert.Equal(later, run.EndedAt);
        Assert.Equal(later, run.UpdatedAt);
        Assert.Equal(4, run.StepNumber);
    }

    [Fact]
    public void CloseKeepsWhatTheEngineCapturedWithTheProcessorsOutcome()
    {
        var run = NewRun(RunState.Initial(RunVariables.Empty()));
        var later = Now.AddMinutes(2);

        run.Close(new RunState(RunStatus.Waiting, "n2", 3, 0, RunVariables.Parse("""{"cedula":"10203"}"""), []), RunStatus.Failed, "messaging.message.rejected", later);

        Assert.Equal((RunStatus.Failed, "messaging.message.rejected", "n2", 3), (run.Status, run.FailureCode, run.CurrentNodeId, run.StepNumber));
        Assert.Equal("10203", RunVariables.Parse(run.VariablesJson).TextOf("cedula"));
        Assert.Equal(later, run.EndedAt);
        Assert.Throws<InvalidOperationException>(() => run.Close(run.ToState(), RunStatus.Interrupted, null, later));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewRun(RunState.Initial(RunVariables.Empty())).Close(run.ToState(), RunStatus.Waiting, null, later));
    }

    [Fact]
    public void ApplyALiveStateKeepsItOpen()
    {
        var run = NewRun(RunState.Initial(RunVariables.Empty()));

        run.Apply(new RunState(RunStatus.Waiting, "ask", 2, 1, RunVariables.Empty(), []), Now.AddMinutes(1));

        Assert.True(run.IsLive);
        Assert.Null(run.EndedAt);
        Assert.Equal(1, run.Attempts);
    }

    [Fact]
    public void FinishClosesTheRunWithTheFailureCode()
    {
        var run = NewRun(RunState.Initial(RunVariables.Empty()));
        var later = Now.AddHours(25);

        run.Finish(RunStatus.Failed, "automations.run.version_missing", later);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal("automations.run.version_missing", run.FailureCode);
        Assert.Equal(later, run.EndedAt);
    }

    [Fact]
    public void FinishRejectsARunThatAlreadyEnded()
    {
        var run = NewRun(RunState.Initial(RunVariables.Empty()));
        run.Finish(RunStatus.Interrupted, null, Now);

        Assert.Throws<InvalidOperationException>(() => run.Finish(RunStatus.Failed, "x", Now.AddMinutes(1)));
    }

    [Fact]
    public void FinishCannotLeaveTheRunAlive() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NewRun(RunState.Initial(RunVariables.Empty())).Finish(RunStatus.Waiting, null, Now));

    [Fact]
    public void ToStateRoundTripsVariablesAndTrace()
    {
        var trace = new[]
        {
            new TraceEntry(Now, "start", "start", "next"),
            new TraceEntry(Now.AddSeconds(2), "ask", "openQuestion", TraceOutcomes.Waiting),
        };
        var run = NewRun(RunState.Initial(RunVariables.Empty()));
        run.Apply(new RunState(RunStatus.Waiting, "ask", 2, 1, RunVariables.Parse("""{"nombre":"Ana","edad":3}"""), trace), Now.AddSeconds(2));

        var state = run.ToState();

        Assert.Equal(RunStatus.Waiting, state.Status);
        Assert.Equal("ask", state.CurrentNodeId);
        Assert.Equal(2, state.StepNumber);
        Assert.Equal(1, state.Attempts);
        Assert.Equal("Ana", state.Variables.TextOf("nombre"));
        Assert.Equal(trace, state.Trace);
        // El contrato del jsonb (§4.2): camelCase con at/nodeId/kind/outcome.
        Assert.Contains("\"nodeId\":\"ask\"", run.TraceJson);
        Assert.Contains("\"at\":", run.TraceJson);
        Assert.Contains("\"outcome\":\"waiting\"", run.TraceJson);
    }
}
