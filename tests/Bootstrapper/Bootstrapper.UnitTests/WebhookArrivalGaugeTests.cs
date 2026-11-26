using System.Diagnostics.Metrics;
using Bootstrapper.Observability;
using BuildingBlocks.Application;
using BuildingBlocks.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-11-16: <c>qep.webhook.last_received</c> publica, por proveedor, la hora Unix del último webhook de Meta, y deja
/// de publicar cuando la lectura pasó de 60 s, como los gauges de las colas: un muestreador caído no pinta un dato viejo como actual.</summary>
public sealed class WebhookArrivalGaugeTests
{
    private const string InstrumentName = "qep.webhook.last_received";

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    /// <summary>Filtra por <c>Meter.Scope</c> (el factory de la prueba) para no cruzarse con otras pruebas en paralelo.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string? Provider, double Value)> _measurements = [];

        public Recorder(IMeterFactory factory)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Meter.Name == QepObservability.MeterName
                    && instrument.Name == InstrumentName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            {
                string? provider = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "provider")
                    {
                        provider = tag.Value as string;
                    }
                }

                lock (_measurements)
                {
                    _measurements.Add((provider, value));
                }
            });
            _listener.Start();
        }

        public IReadOnlyList<(string? Provider, double Value)> Collect()
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
    }

    private static (IMeterFactory Factory, ServiceProvider Provider) NewFactory()
    {
        var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        return (provider.GetRequiredService<IMeterFactory>(), provider);
    }

    [Fact]
    public void PublishesTheUnixSecondsPerProvider()
    {
        var (factory, provider) = NewFactory();
        using var _ = provider;
        var gauge = new WebhookArrivalGauge(factory, new MutableClock(DateTimeOffset.UtcNow));
        using var recorder = new Recorder(factory);

        gauge.Record(new Dictionary<string, DateTimeOffset>
        {
            ["whatsapp"] = new(2026, 11, 16, 15, 0, 0, TimeSpan.Zero),
            ["instagram"] = new(2026, 11, 16, 14, 0, 0, TimeSpan.Zero),
        });

        var measurements = recorder.Collect();
        // 2026-11-16T15:00:00Z and 14:00:00Z in Unix seconds.
        Assert.Equal(1_794_841_200d, Assert.Single(measurements, m => m.Provider == "whatsapp").Value);
        Assert.Equal(1_794_837_600d, Assert.Single(measurements, m => m.Provider == "instagram").Value);
    }

    [Fact]
    public void AProviderMissingFromTheReadingIsNotPublished()
    {
        var (factory, provider) = NewFactory();
        using var _ = provider;
        var gauge = new WebhookArrivalGauge(factory, new MutableClock(DateTimeOffset.UtcNow));
        using var recorder = new Recorder(factory);

        gauge.Record(new Dictionary<string, DateTimeOffset> { ["whatsapp"] = DateTimeOffset.UtcNow });
        gauge.Record(new Dictionary<string, DateTimeOffset> { ["instagram"] = DateTimeOffset.UtcNow });

        Assert.Equal("instagram", Assert.Single(recorder.Collect()).Provider);
    }

    [Fact]
    public void AStaleReadingIsNotPublished()
    {
        var (factory, provider) = NewFactory();
        using var _ = provider;
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var gauge = new WebhookArrivalGauge(factory, clock);
        using var recorder = new Recorder(factory);

        gauge.Record(new Dictionary<string, DateTimeOffset> { ["whatsapp"] = clock.UtcNow });
        clock.UtcNow += TimeSpan.FromSeconds(61);

        Assert.Empty(recorder.Collect());
    }
}
