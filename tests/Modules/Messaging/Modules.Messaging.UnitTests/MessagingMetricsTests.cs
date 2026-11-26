using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Infrastructure.Webhook;

namespace Modules.Messaging.UnitTests;

/// <summary>
/// Las series que lee <c>QepWebhookDiscarding</c> nacen en cero al crear las métricas. <c>increase()</c> toma la primera muestra de
/// una serie como base: sin la semilla, el primer lote de descartes después de cada reinicio del pod —justo la ventana de un
/// despliegue— no contaría para la alerta.
/// </summary>
public sealed class MessagingMetricsTests
{
    [Fact]
    public void TheAlertingSeriesStartAtZero()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        var measurements = new List<(long Value, string? Provider, string? Reason)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Name == "qep.webhook.discarded")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? providerTag = null;
            string? reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "provider")
                {
                    providerTag = tag.Value as string;
                }
                else if (tag.Key == "reason")
                {
                    reason = tag.Value as string;
                }
            }

            measurements.Add((value, providerTag, reason));
        });
        listener.Start();

        _ = new MessagingMetrics(factory);

        Assert.Equal(
            new[]
            {
                (0L, "instagram", "unknown_route"),
                (0L, "instagram", "without_user_id"),
                (0L, "whatsapp", "unknown_route"),
                (0L, "whatsapp", "without_user_id"),
            },
            measurements.Select(m => (m.Value, m.Provider!, m.Reason!)).Order());
    }
}
