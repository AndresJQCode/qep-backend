using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using BuildingBlocks.Application;
using BuildingBlocks.Observability;

namespace Bootstrapper.Observability;

/// <summary>
/// Observabilidad fase 2: los tres gauges de las colas (<c>qep.queue.backlog</c>, <c>qep.queue.retrying</c>, <c>qep.queue.oldest_age</c>,
/// etiqueta <c>queue</c>). Guardan la última lectura de cada cola y la publican cada vez que el exportador pregunta. Una lectura de más
/// de <see cref="MaxReadingAge"/> deja de publicarse: si el muestreador se cae (o una cola no se puede leer), el panel muestra un hueco y
/// no un atraso viejo pintado como actual. Singleton: no lleva etiqueta de tenant ni dato personal.
/// </summary>
public sealed class QueueGauges
{
    /// <summary>Cuatro vueltas del muestreador (15 s): pasado esto, la lectura ya no describe la cola de ahora.</summary>
    internal static readonly TimeSpan MaxReadingAge = TimeSpan.FromSeconds(60);

    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, (QueueReading Reading, DateTimeOffset At)> _latest = new(StringComparer.Ordinal);

    public QueueGauges(IMeterFactory meterFactory, IClock clock)
    {
        _clock = clock;
        Meter = meterFactory.Create(QepObservability.MeterName);
        Meter.CreateObservableGauge("qep.queue.backlog", () => Fresh(r => r.Backlog),
            description: "Filas pendientes de la cola asíncrona.");
        Meter.CreateObservableGauge("qep.queue.retrying", () => Fresh(r => r.Retrying),
            description: "Filas de la cola que ya se intentaron y reintentan.");
        Meter.CreateObservableGauge("qep.queue.oldest_age", () => FreshAge(), unit: "s",
            description: "Segundos de la pendiente más vieja que nadie ha intentado; sin dato en colas sin columna de edad.");
    }

    public Meter Meter { get; }

    public void Record(QueueReading reading) => _latest[reading.Queue] = (reading, _clock.UtcNow);

    private IEnumerable<Measurement<long>> Fresh(Func<QueueReading, long> select)
    {
        var now = _clock.UtcNow;
        foreach (var (queue, (reading, at)) in _latest)
        {
            if (now - at > MaxReadingAge)
            {
                continue;
            }

            yield return new Measurement<long>(select(reading), new KeyValuePair<string, object?>("queue", queue));
        }
    }

    private IEnumerable<Measurement<double>> FreshAge()
    {
        var now = _clock.UtcNow;
        foreach (var (queue, (reading, at)) in _latest)
        {
            if (now - at > MaxReadingAge || reading.OldestAgeSeconds is not { } age)
            {
                continue;
            }

            yield return new Measurement<double>(age, new KeyValuePair<string, object?>("queue", queue));
        }
    }
}
