using System.Diagnostics.Metrics;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Métricas de Messaging. El meter lleva el prefijo <c>Qep.</c>, que el exportador ya recoge: el módulo no referencia
/// la infraestructura de observabilidad. Sin etiqueta de tenant ni datos de la persona.</summary>
internal sealed class MessagingMetrics
{
    public const string MeterName = "Qep.Messaging";

    private readonly Histogram<double> _inboundLag;
    private readonly Counter<long> _discarded;

    public MessagingMetrics(IMeterFactory meterFactory)
    {
        Meter = meterFactory.Create(MeterName);
        _inboundLag = Meter.CreateHistogram(
            "qep.messaging.inbound.lag",
            unit: "s",
            description: "Seconds between Meta's timestamp for an inbound message and the moment it was stored.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [1, 2, 5, 10, 30, 60, 300, 3600] });
        _discarded = Meter.CreateCounter<long>(
            "qep.webhook.discarded",
            unit: "{event}",
            description: "Webhook events from Meta that were received and deliberately not ingested.");
    }

    public Meter Meter { get; }

    /// <summary>Un timestamp a futuro (reloj desfasado de Meta o del pod) cuenta cero: un rezago negativo no existe.</summary>
    public void RecordInboundLag(string channel, DateTimeOffset occurredAt, DateTimeOffset storedAt) =>
        _inboundLag.Record(Math.Max(0, (storedAt - occurredAt).TotalSeconds), new KeyValuePair<string, object?>("channel", channel));

    /// <summary><paramref name="provider"/> y <paramref name="reason"/> salen de <see cref="WebhookDiscardProviders"/> y
    /// <see cref="WebhookDiscardReasons"/>. Un descarte de cero eventos (un change de sólo estados) no suma.</summary>
    public void RecordDiscarded(string provider, string reason, int count)
    {
        if (count <= 0)
        {
            return;
        }

        _discarded.Add(count, new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("reason", reason));
    }
}
