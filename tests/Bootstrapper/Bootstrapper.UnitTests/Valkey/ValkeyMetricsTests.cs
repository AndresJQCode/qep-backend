using System.Diagnostics.Metrics;
using Bootstrapper.Valkey;
using BuildingBlocks.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// <c>qep.valkey.fallbacks</c> lleva el motivo además del área: en producción el motivo sólo estaba en un log
/// <c>Debug</c>, y los fallbacks de horario laboral (2026-11-23) no se podían atribuir a un tope de 100 ms o a una
/// desconexión. Sin Docker: el contador se prueba solo.
/// </summary>
public sealed class ValkeyMetricsTests
{
    [Fact]
    public async Task AFallbackCarriesTheAreaAndTheReason()
    {
        await using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        var options = Options.Create(new ValkeyOptions { Enabled = true });
        await using var connection = new ValkeyConnection(
            new ConfigurationBuilder().Build(), options, NullLogger<ValkeyConnection>.Instance);
        var metrics = new ValkeyMetrics(factory, connection);
        var seen = new List<(string? Area, string? Reason, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, factory)
                && instrument.Meter.Name == QepObservability.MeterName
                && instrument.Name == "qep.valkey.fallbacks")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? area = null;
            string? reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "area")
                {
                    area = tag.Value as string;
                }
                else if (tag.Key == "reason")
                {
                    reason = tag.Value as string;
                }
            }

            seen.Add((area, reason, value));
        });
        listener.Start();

        metrics.RecordFallback(ValkeyArea.Presence, "TimeoutException");
        metrics.RecordFallback(ValkeyArea.RateLimiting, "NotConnected");

        Assert.Equal(
            [("presence", "TimeoutException", 1L), ("rate_limiting", "NotConnected", 1L)],
            seen);
    }

    // Los límites de los buckets son los que dejan ver si una operación que pasó el tope de 100 ms terminó a los 120 ms o a
    // los 2 s: con los buckets por defecto de OpenTelemetry (pensados en milisegundos) todo caería en el primero.
    [Fact]
    public async Task AnOperationRecordsItsDurationInSecondsWithBucketsAroundTheTimeout()
    {
        await using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        var options = Options.Create(new ValkeyOptions { Enabled = true });
        await using var connection = new ValkeyConnection(
            new ConfigurationBuilder().Build(), options, NullLogger<ValkeyConnection>.Instance);
        var metrics = new ValkeyMetrics(factory, connection);
        var seen = new List<(string? Area, string? Outcome, double Value)>();
        IReadOnlyList<double>? boundaries = null;
        string? unit = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, factory)
                && instrument is Histogram<double> { Name: "qep.valkey.operation.duration" } histogram)
            {
                boundaries = histogram.Advice?.HistogramBucketBoundaries;
                unit = histogram.Unit;
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string? area = null;
            string? outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "area")
                {
                    area = tag.Value as string;
                }
                else if (tag.Key == "outcome")
                {
                    outcome = tag.Value as string;
                }
            }

            seen.Add((area, outcome, value));
        });
        listener.Start();

        metrics.RecordOperation(ValkeyArea.Sessions, ValkeyOperationOutcome.Late, TimeSpan.FromMilliseconds(120));

        Assert.Equal([("sessions", "late", 0.12)], seen);
        Assert.Equal("s", unit);
        Assert.NotNull(boundaries);
        Assert.Superset(new HashSet<double> { 0.1, 0.125, 0.15, 0.25, 0.5, 1 }, boundaries.ToHashSet());
    }
}
