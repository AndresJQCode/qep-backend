using System.Diagnostics.Metrics;
using BuildingBlocks.Observability;
using Bootstrapper.Valkey;
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
}
