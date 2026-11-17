using System.Diagnostics.Metrics;
using BuildingBlocks.Application;
using BuildingBlocks.Observability;

namespace Bootstrapper.Observability;

/// <summary>
/// Spec 2026-11-16: <c>qep.webhook.last_received</c>, la hora Unix (segundos) del último webhook de Meta por proveedor (etiqueta
/// <c>provider</c>). Guarda la última lectura completa de <see cref="QueueSamplerWorker"/> y la publica cuando el exportador pregunta;
/// pasado <see cref="QueueGauges.MaxReadingAge"/> deja de publicarla, como los gauges de las colas. Un proveedor sin entregas no
/// publica (el panel dice «No data», no 1970). Singleton, sin tenant ni dato personal.
/// </summary>
public sealed class WebhookArrivalGauge
{
    private readonly IClock _clock;
    private volatile Reading? _latest;

    public WebhookArrivalGauge(IMeterFactory meterFactory, IClock clock)
    {
        _clock = clock;
        Meter = meterFactory.Create(QepObservability.MeterName);
        Meter.CreateObservableGauge("qep.webhook.last_received", Observe, unit: "s",
            description: "Unix time of the newest Meta webhook delivery accepted, per provider.");
    }

    public Meter Meter { get; }

    /// <summary>Reemplaza la lectura entera: un proveedor que ya no viene deja de publicarse.</summary>
    public void Record(IReadOnlyDictionary<string, DateTimeOffset> lastReceived) => _latest = new Reading(lastReceived, _clock.UtcNow);

    private IEnumerable<Measurement<double>> Observe()
    {
        if (_latest is not { } latest || _clock.UtcNow - latest.At > QueueGauges.MaxReadingAge)
        {
            yield break;
        }

        foreach (var (provider, receivedAt) in latest.LastReceived)
        {
            yield return new Measurement<double>(receivedAt.ToUnixTimeSeconds(), new KeyValuePair<string, object?>("provider", provider));
        }
    }

    private sealed record Reading(IReadOnlyDictionary<string, DateTimeOffset> LastReceived, DateTimeOffset At);
}
