using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Modules.Siigo.Domain;

namespace Modules.Siigo.Infrastructure.Api;

/// <summary>
/// Spec 2026-11-21: <c>qep_siigo_call_failures_total{operation,reason}</c>, una por cada llamada a Siigo que falla. El meter lleva el
/// prefijo <c>Qep.</c>, que el exportador ya recoge (como <c>ShopifyMetrics</c>): el módulo no referencia la infraestructura de
/// observabilidad. <b>Sin tenant, conexión, factura ni ruta</b> (cardinalidad): el tenant se busca en <c>siigo.call_failures</c> o por
/// <c>TraceId</c>.
/// <para>
/// Es un <see cref="ObservableCounter{T}"/> sembrado en cero para cada operación × los motivos fijos: <c>increase()</c> de Prometheus
/// toma la primera muestra de una serie como base, así que una serie que nace al ocurrir la primera falla (justo la de credenciales
/// rechazadas tras un reinicio) no dispararía su alerta. Los <c>http_&lt;status&gt;</c> nacen al ocurrir.
/// </para>
/// </summary>
internal sealed class SiigoMetrics
{
    public const string MeterName = "Qep.Siigo";

    /// <summary>Los motivos que <c>SiigoApiClient</c> produce sin un status HTTP; sembrados para que su alerta funcione desde el primer evento.</summary>
    private static readonly string[] FixedReasons =
        ["timeout", "network", "credentials_rejected", "invalid_response", "partner_id_missing", "auth_without_token"];

    private readonly ConcurrentDictionary<(string Operation, string Reason), StrongBox<long>> _failures = new();

    public SiigoMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        foreach (var operation in Enum.GetValues<SiigoCallOperation>())
        {
            foreach (var reason in FixedReasons)
            {
                _failures.TryAdd((operation.ToString(), reason), new StrongBox<long>());
            }
        }

        Meter = meterFactory.Create(MeterName);
        Meter.CreateObservableCounter(
            "qep.siigo.call_failures",
            Observe,
            unit: "{call}",
            description: "Failed calls to Siigo by operation and reason.");
    }

    public Meter Meter { get; }

    /// <summary>Un <paramref name="reason"/> vacío (un <c>http_</c> sin status) se cuenta como <c>unknown</c>, no como una serie propia.</summary>
    public void RecordFailure(SiigoCallOperation operation, string reason)
    {
        var normalized = string.IsNullOrWhiteSpace(reason) || reason == "http_" ? "unknown" : reason;
        var box = _failures.GetOrAdd((operation.ToString(), normalized), static _ => new StrongBox<long>());
        Interlocked.Increment(ref box.Value);
    }

    private IEnumerable<Measurement<long>> Observe()
    {
        foreach (var ((operation, reason), box) in _failures)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref box.Value),
                new KeyValuePair<string, object?>("operation", operation),
                new KeyValuePair<string, object?>("reason", reason));
        }
    }
}
