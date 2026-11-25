using System.Diagnostics.Metrics;
using BuildingBlocks.Observability;

namespace Bootstrapper.Valkey;

/// <summary>Cómo terminó una operación contra Valkey: la etiqueta <c>outcome</c> de <c>qep.valkey.operation.duration</c>.</summary>
internal enum ValkeyOperationOutcome
{
    /// <summary><c>ok</c>: respondió dentro de <c>OperationTimeoutMs</c>.</summary>
    Ok,

    /// <summary><c>error</c>: falló dentro de <c>OperationTimeoutMs</c>.</summary>
    Error,

    /// <summary><c>late</c>: pasó el tope, el que llamaba ya usó su respaldo, y después respondió.</summary>
    Late,

    /// <summary><c>late_error</c>: pasó el tope y después falló.</summary>
    LateError,
}

/// <summary>
/// Las métricas de Valkey (spec 2026-11-13, D-1) en el meter de la plataforma: el gauge <c>qep.valkey.connected</c>
/// (0/1; en Prometheus <c>qep_valkey_connected</c>), el contador <c>qep.valkey.fallbacks</c> con etiquetas <c>area</c> y
/// <c>reason</c> (<c>qep_valkey_fallbacks_total{area,reason}</c>) y el histograma <c>qep.valkey.operation.duration</c> en
/// segundos con <c>area</c> y <c>outcome</c> (<c>qep_valkey_operation_duration_seconds</c>). Valkey no entra a
/// <c>/health/ready</c>: una caída se ve acá, no sacando pods.
/// </summary>
internal sealed class ValkeyMetrics
{
    private readonly Counter<long> _fallbacks;
    private readonly Histogram<double> _operationDuration;

    public ValkeyMetrics(IMeterFactory meterFactory, ValkeyConnection connection)
    {
        var meter = meterFactory.Create(QepObservability.MeterName);
        meter.CreateObservableGauge(
            "qep.valkey.connected",
            () => connection.Multiplexer?.IsConnected == true ? 1 : 0,
            description: "1 si el cliente de Valkey del pod está conectado; 0 si está apagado, sin cadena o desconectado.");
        _fallbacks = meter.CreateCounter<long>(
            "qep.valkey.fallbacks",
            description: "Operaciones que no llegaron a Valkey y usaron el respaldo local del área.");
        // Límites en segundos, finos alrededor del tope de 100 ms: los de OpenTelemetry por defecto son de milisegundos.
        _operationDuration = meter.CreateHistogram(
            "qep.valkey.operation.duration",
            unit: "s",
            description: "Cuánto tarda cada operación contra Valkey, incluidas las que terminan después del tope.",
            tags: null,
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries =
                    [0.001, 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.125, 0.15, 0.2, 0.25, 0.35, 0.5, 1, 2, 5],
            });
    }

    /// <summary>
    /// La duración se mide desde el pod: incluye la red, el servidor y lo que el pod tarde en atender la respuesta (pausas
    /// de GC, CPU frenada, hilos ocupados). Una operación que pasó el tope se registra cuando termina, no cuando se suelta.
    /// </summary>
    public void RecordOperation(ValkeyArea area, ValkeyOperationOutcome outcome, TimeSpan elapsed) =>
        _operationDuration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("area", ValkeyAreas.TagOf(area)),
            new KeyValuePair<string, object?>("outcome", TagOf(outcome)));

    private static string TagOf(ValkeyOperationOutcome outcome) => outcome switch
    {
        ValkeyOperationOutcome.Ok => "ok",
        ValkeyOperationOutcome.Error => "error",
        ValkeyOperationOutcome.Late => "late",
        ValkeyOperationOutcome.LateError => "late_error",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    /// <summary>
    /// <paramref name="reason"/> es <c>NotConnected</c> o el nombre del tipo de la excepción (<c>TimeoutException</c>,
    /// <c>RedisConnectionException</c>, <c>RedisServerException</c> para un <c>NOPERM</c>…): un conjunto chico y cerrado, así
    /// que la cardinalidad no preocupa. Nunca el mensaje, que lleva <c>host:puerto</c>.
    /// </summary>
    public void RecordFallback(ValkeyArea area, string reason) =>
        _fallbacks.Add(
            1,
            new KeyValuePair<string, object?>("area", ValkeyAreas.TagOf(area)),
            new KeyValuePair<string, object?>("reason", reason));
}
