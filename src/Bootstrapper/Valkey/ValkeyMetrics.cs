using System.Diagnostics.Metrics;
using BuildingBlocks.Observability;

namespace Bootstrapper.Valkey;

/// <summary>
/// Las dos métricas de Valkey (spec 2026-11-13, D-1) en el meter de la plataforma: el gauge <c>qep.valkey.connected</c>
/// (0/1; en Prometheus <c>qep_valkey_connected</c>) y el contador <c>qep.valkey.fallbacks</c> con etiquetas <c>area</c> y
/// <c>reason</c> (<c>qep_valkey_fallbacks_total{area,reason}</c>). Valkey no entra a <c>/health/ready</c>: una caída se ve acá, no sacando pods.
/// </summary>
internal sealed class ValkeyMetrics
{
    private readonly Counter<long> _fallbacks;

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
    }

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
