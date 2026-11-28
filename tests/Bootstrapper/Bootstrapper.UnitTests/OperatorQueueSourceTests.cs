using Bootstrapper.Observability;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootstrapper.UnitTests;

/// <summary>Colas de la consola de plataforma: orden, aislamiento de fallas, tope total y cancelación de la petición.</summary>
public sealed class OperatorQueueSourceTests
{
    private sealed class FakeQueues(Func<string, CancellationToken, Task<QueueReading>>? read = null) : IQueueMetricsSource
    {
        public List<string> Asked { get; } = [];

        public Task<QueueReading> ReadAsync(string queue, CancellationToken cancellationToken)
        {
            Asked.Add(queue);
            return read is null
                ? Task.FromResult(new QueueReading(queue, 5, 1, queue == QueueNames.Campaign ? null : 3))
                : read(queue, cancellationToken);
        }
    }

    private static OperatorQueueSource Build(FakeQueues queues, TimeSpan? budget = null) =>
        new(queues, NullLogger<OperatorQueueSource>.Instance, budget ?? TimeSpan.FromSeconds(10));

    [Fact]
    public async Task AllTheQueuesTravelInTheOrderOfQueueNames()
    {
        var queues = new FakeQueues();

        var rows = await Build(queues).ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(QueueNames.All, rows.Select(row => row.Queue));
        Assert.Equal(QueueNames.All, queues.Asked);
        Assert.All(rows, row => Assert.Equal(5, row.Backlog));
        Assert.All(rows, row => Assert.Equal(1, row.Retrying));
        Assert.Null(rows.Single(row => row.Queue == QueueNames.Campaign).OldestAgeSeconds);
        Assert.Equal(3, rows.Single(row => row.Queue == QueueNames.Webhook).OldestAgeSeconds);
    }

    [Fact]
    public async Task AFailingQueueGoesWithThreeNullsAndTheOthersAnswer()
    {
        var queues = new FakeQueues((queue, _) => queue == QueueNames.Media
            ? throw new InvalidOperationException("boom")
            : Task.FromResult(new QueueReading(queue, 2, 0, 1)));

        var rows = await Build(queues).ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(QueueNames.All.Count, rows.Count);
        var media = rows.Single(row => row.Queue == QueueNames.Media);
        Assert.Null(media.Backlog);
        Assert.Null(media.Retrying);
        Assert.Null(media.OldestAgeSeconds);
        Assert.All(rows.Where(row => row.Queue != QueueNames.Media), row => Assert.Equal(2, row.Backlog));
        Assert.Equal(QueueNames.All, queues.Asked);
    }

    [Fact]
    public async Task AnExpiredBudgetLeavesTheCurrentAndTheRemainingQueuesNullWithoutAskingThem()
    {
        // La tercera cola (media) se queda esperando hasta que vence el tope; las que faltan ni se consultan.
        var queues = new FakeQueues(async (queue, token) =>
        {
            if (queue == QueueNames.Media)
            {
                await Task.Delay(Timeout.Infinite, token);
            }

            return new QueueReading(queue, 4, 0, 1);
        });

        var rows = await Build(queues, TimeSpan.FromMilliseconds(200)).ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(QueueNames.All, rows.Select(row => row.Queue));
        Assert.All(rows.Take(2), row => Assert.Equal(4, row.Backlog));
        Assert.All(rows.Skip(2), row =>
        {
            Assert.Null(row.Backlog);
            Assert.Null(row.Retrying);
            Assert.Null(row.OldestAgeSeconds);
        });
        Assert.Equal(QueueNames.All.Take(3), queues.Asked);
    }

    [Fact]
    public async Task ACancelledRequestPropagatesInsteadOfReturningNulls()
    {
        using var cts = new CancellationTokenSource();
        var queues = new FakeQueues(async (queue, token) =>
        {
            await cts.CancelAsync();
            token.ThrowIfCancellationRequested();
            return new QueueReading(queue, 1, 0, 0);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(queues).ListAsync(cts.Token));
    }
}
