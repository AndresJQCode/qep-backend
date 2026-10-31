using System.Diagnostics.Metrics;
using BuildingBlocks.Application;
using BuildingBlocks.Observability;
using Bootstrapper.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace Bootstrapper.UnitTests;

/// <summary>Los tres gauges de las colas: publican la última lectura y dejan de publicar una cola cuya lectura pasó de 60 s, para que
/// un muestreador caído no deje un atraso viejo pintado como si fuera de ahora.</summary>
public sealed class QueueGaugesTests
{
    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    /// <summary>Filtra por <c>Meter.Scope</c> (el factory de la prueba) para no cruzarse con otras pruebas en paralelo.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, string? Queue, double Value)> _measurements = [];

        public Recorder(IMeterFactory factory)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Meter.Name == QepObservability.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.Start();
        }

        public IReadOnlyList<(string Instrument, string? Queue, double Value)> Collect()
        {
            lock (_measurements)
            {
                _measurements.Clear();
            }

            _listener.RecordObservableInstruments();
            lock (_measurements)
            {
                return [.. _measurements];
            }
        }

        public void Dispose() => _listener.Dispose();

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? queue = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "queue")
                {
                    queue = tag.Value as string;
                }
            }

            lock (_measurements)
            {
                _measurements.Add((instrument.Name, queue, value));
            }
        }
    }

    private static (IMeterFactory Factory, ServiceProvider Provider) NewFactory()
    {
        var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        return (provider.GetRequiredService<IMeterFactory>(), provider);
    }

    [Fact]
    public void PublishesTheLastReading()
    {
        var (factory, provider) = NewFactory();
        using var _ = provider;
        var gauges = new QueueGauges(factory, new MutableClock(DateTimeOffset.UtcNow));
        using var recorder = new Recorder(factory);

        gauges.Record(new QueueReading(QueueNames.Webhook, 7, 2, 12.5));
        gauges.Record(new QueueReading(QueueNames.Webhook, 9, 3, 20));

        var measurements = recorder.Collect();
        Assert.Equal(9, Assert.Single(measurements, m => m.Instrument == "qep.queue.backlog" && m.Queue == "webhook").Value);
        Assert.Equal(3, Assert.Single(measurements, m => m.Instrument == "qep.queue.retrying" && m.Queue == "webhook").Value);
        Assert.Equal(20, Assert.Single(measurements, m => m.Instrument == "qep.queue.oldest_age" && m.Queue == "webhook").Value);
    }

    [Fact]
    public void ACampaignPublishesNoAge()
    {
        var (factory, provider) = NewFactory();
        using var _ = provider;
        var gauges = new QueueGauges(factory, new MutableClock(DateTimeOffset.UtcNow));
        using var recorder = new Recorder(factory);

        gauges.Record(new QueueReading(QueueNames.Campaign, 4, 1, null));

        var measurements = recorder.Collect();
        Assert.Single(measurements, m => m.Instrument == "qep.queue.backlog" && m.Queue == "campaign");
        Assert.Single(measurements, m => m.Instrument == "qep.queue.retrying" && m.Queue == "campaign");
        Assert.DoesNotContain(measurements, m => m.Instrument == "qep.queue.oldest_age");
    }

    [Fact]
    public void AReadingOlderThanSixtySecondsStopsPublishing()
    {
        var (factory, provider) = NewFactory();
        using var _ = provider;
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var gauges = new QueueGauges(factory, clock);
        using var recorder = new Recorder(factory);

        gauges.Record(new QueueReading(QueueNames.Webhook, 1, 0, 1));
        clock.UtcNow += TimeSpan.FromSeconds(40);
        gauges.Record(new QueueReading(QueueNames.Media, 2, 0, 2));
        clock.UtcNow += TimeSpan.FromSeconds(21); // webhook va en 61 s, media en 21 s.

        var measurements = recorder.Collect();
        Assert.DoesNotContain(measurements, m => m.Queue == "webhook");
        Assert.Equal(3, measurements.Count(m => m.Queue == "media"));
    }
}
