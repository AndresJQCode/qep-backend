using System.Diagnostics.Metrics;
using BuildingBlocks.Application;
using BuildingBlocks.Observability;
using Bootstrapper.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootstrapper.UnitTests;

public sealed class QueueSamplerWorkerTests
{
    private sealed class FixedNowClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class FakeSource(string? failingQueue) : IQueueMetricsSource
    {
        public Task<QueueReading> ReadAsync(string queue, CancellationToken cancellationToken)
        {
            if (queue == failingQueue)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(new QueueReading(queue, 5, 1, queue == QueueNames.Campaign ? null : 3));
        }
    }

    [Fact]
    public async Task OneFailingQueueDoesNotStopTheOthers()
    {
        using var provider = new ServiceCollection()
            .AddMetrics()
            .AddScoped<IQueueMetricsSource>(_ => new FakeSource(QueueNames.Media))
            .BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        var gauges = new QueueGauges(factory, new FixedNowClock());
        var worker = new QueueSamplerWorker(provider.GetRequiredService<IServiceScopeFactory>(), gauges, NullLogger<QueueSamplerWorker>.Instance);

        var queues = new HashSet<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Meter.Name == QepObservability.MeterName
                && instrument.Name == "qep.queue.backlog")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "queue")
                {
                    lock (queues)
                    {
                        queues.Add((string)tag.Value!);
                    }
                }
            }
        });
        listener.Start();

        await worker.SampleAsync(TestContext.Current.CancellationToken);
        listener.RecordObservableInstruments();

        Assert.Equal(
            QueueNames.All.Where(q => q != QueueNames.Media).Order(),
            queues.Order());
    }
}
