using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-10-25 D-E5, D-E8 y D-E14: la fila de <c>run_units</c> que arma el procesador, y que armarla nunca lanza.</summary>
public sealed class RunUnitWriterTests
{
    private static readonly IReadOnlySet<string> NoAttachments = new HashSet<string>();

    private static ConversationRun Run() =>
        ConversationRun.Start(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            RunState.Initial(RunVariables.Empty()) with { Status = RunStatus.Waiting, CurrentNodeId = "q", StepNumber = 3 }, DateTimeOffset.UtcNow);

    [Fact]
    public void TheRowFitsTheCaps()
    {
        var units = new MemoryRunUnits();
        var log = new RunUnitLog();
        log.Record(
            new StepResult(RunState.Initial(RunVariables.Empty()), [])
            {
                Steps = [new StepRecord(1, StepSeqs.Exit, "q", "openQuestion", "answered", [new StepWrite("doc", JsonValue.Create(new string('a', 9 * 1024)))], [], false, null, null)],
            },
            new EngineInput.Start());
        var run = Run();
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-1);

        new RunUnitWriter(units, new FixedClock(), new NoLogger()).Write(
            run, 3, RunUnitTriggers.CustomerMessage, Guid.CreateVersion7(), RunOutcomes.Advanced, startedAt, log, NoAttachments);

        var row = Assert.Single(units.Entries);
        Assert.Equal((run.TenantId, run.Id, 3, startedAt), (row.TenantId, row.RunId, row.BaseStep, row.StartedAt));
        var write = Assert.Single(Assert.Single(RunUnitJson.Parse(row.StepsJson)).Writes);
        Assert.True(write.Truncated);
        Assert.Null(write.Value);
    }

    [Fact]
    public void AFailureIsLoggedAndNotThrown()
    {
        var units = new MemoryRunUnits { Failure = new InvalidOperationException("x") };
        var logger = new CountingLogger();
        var writer = new RunUnitWriter(units, new FixedClock(), logger);
        var run = Run();

        writer.Write(run, 0, RunUnitTriggers.Start, null, RunOutcomes.Started, DateTimeOffset.UtcNow, null, NoAttachments);
        writer.WriteClosed(run, null, RunCloseReasons.Abandoned, DateTimeOffset.UtcNow);

        Assert.Equal(2, logger.Warnings);
    }

    private sealed class MemoryRunUnits : IRunUnitRepository
    {
        public List<RunUnitEntry> Entries { get; } = [];

        public Exception? Failure { get; init; }

        public void Add(RunUnitEntry entry)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Entries.Add(entry);
        }

        public Task<IReadOnlyList<RunUnitEntry>> ListAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> PurgeBeforeAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class NoLogger : ILogger<RunUnitWriter>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class CountingLogger : ILogger<RunUnitWriter>
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }
}
