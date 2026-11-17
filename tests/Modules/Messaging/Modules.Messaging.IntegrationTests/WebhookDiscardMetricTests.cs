using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-11-16 (monitoreo de webhooks): un evento de Meta que llega con firma válida y no se ingresa suma en
/// <c>qep.webhook.discarded</c> con su proveedor y su motivo. Los literales son contrato con las alertas de <c>migracion-k8s</c>.</summary>
public sealed class WebhookDiscardMetricTests
{
    private const string InstrumentName = "qep.webhook.discarded";

    private static long NowSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Las mediciones del host de la prueba: el filtro por <c>Meter.Scope</c> evita que las pruebas en paralelo se crucen.</summary>
    private sealed class DiscardRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(long Value, string? Provider, string? Reason)> _measurements = [];

        public DiscardRecorder(IMeterFactory factory)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == InstrumentName && ReferenceEquals(instrument.Meter.Scope, factory))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                // Los ceros son la semilla de las series de la alerta (MessagingMetricsTests), no descartes.
                if (value == 0)
                {
                    return;
                }

                string? provider = null;
                string? reason = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "provider")
                    {
                        provider = tag.Value as string;
                    }
                    else if (tag.Key == "reason")
                    {
                        reason = tag.Value as string;
                    }
                }

                lock (_measurements)
                {
                    _measurements.Add((value, provider, reason));
                }
            });
            _listener.Start();
        }

        public IReadOnlyList<(long Value, string? Provider, string? Reason)> Measurements
        {
            get
            {
                lock (_measurements)
                {
                    return [.. _measurements];
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed record Fixture(QepApiFactory Factory, HttpClient Anonymous, DiscardRecorder Recorder);

    private static Fixture Watch(QepApiFactory factory) =>
        new(factory, factory.CreateClient(), new DiscardRecorder(factory.Services.GetRequiredService<IMeterFactory>()));

    /// <summary>Tenant con Messaging y la conexión de WhatsApp del número <c>"111"</c>.</summary>
    private static async Task<Fixture> ArrangeWhatsAppAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        return Watch(factory);
    }

    private static async Task PostAndDrainAsync(Fixture f, string body)
    {
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(f.Anonymous, body)).StatusCode);
        await DrainDeliveriesAsync(f.Factory);
    }

    [Fact]
    public async Task AnInboundForAnUnknownNumberCountsAsUnknownRoute()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeWhatsAppAsync(database);
        using var _ = f.Factory;
        using var __ = f.Recorder;

        await PostAndDrainAsync(f, MetaPayloads.Inbound("999", "CO.1349120865530274", "573001234567", "wamid.1", NowSeconds, "hola"));

        Assert.Equal((1L, "whatsapp", "unknown_route"), Assert.Single(f.Recorder.Measurements));
    }

    [Fact]
    public async Task AnInboundWithoutUserIdCountsAsWithoutUserId()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeWhatsAppAsync(database);
        using var _ = f.Factory;
        using var __ = f.Recorder;

        await PostAndDrainAsync(f, MetaPayloads.Inbound("111", "", "573001234567", "wamid.1", NowSeconds, "hola"));

        Assert.Contains((1L, "whatsapp", "without_user_id"), f.Recorder.Measurements);
    }

    [Fact]
    public async Task AStatusOfAnUnknownMessageCountsAsForeignStatus()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeWhatsAppAsync(database);
        using var _ = f.Factory;
        using var __ = f.Recorder;

        await PostAndDrainAsync(f, MetaPayloads.Status("111", "wamid.not-from-qep", "delivered", NowSeconds));

        Assert.Equal((1L, "whatsapp", "foreign_status"), Assert.Single(f.Recorder.Measurements));
    }

    [Fact]
    public async Task AnIngestedInboundCountsNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeWhatsAppAsync(database);
        using var _ = f.Factory;
        using var __ = f.Recorder;

        await PostAndDrainAsync(f, MetaPayloads.Inbound("111", "CO.1349120865530274", "573001234567", "wamid.1", NowSeconds, "hola"));

        Assert.Empty(f.Recorder.Measurements);
    }

    /// <summary>Un mensaje del historial sin id se salta: sin contador, ese descarte era silencioso.</summary>
    [Fact]
    public async Task AHistoryMessageWithoutIdCountsAsHistoryInvalid()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeWhatsAppAsync(database);
        using var _ = f.Factory;
        using var __ = f.Recorder;

        await PostAndDrainAsync(f, MetaPayloads.History("111", 0, 1, 100, ("573001110001", [("", "hola", NowSeconds - 60, false)])));

        Assert.Equal((1L, "whatsapp", "history_invalid"), Assert.Single(f.Recorder.Measurements));
    }

    /// <summary>Un evento que el parser ignora cuenta como <c>ignored</c>, no además como <c>unknown_route</c>: la alerta lee el segundo.</summary>
    [Fact]
    public async Task AnIgnoredInstagramEventForAnUnknownAccountCountsOnlyAsIgnored()
    {
        await using var database = await StartDatabaseAsync();
        var f = Watch(new QepApiFactory(database.GetConnectionString()));
        using var _ = f.Factory;
        using var __ = f.Recorder;

        Assert.Equal(HttpStatusCode.OK, (await PostInstagramWebhookAsync(f.Anonymous, InstagramPayloads.Reaction("m1", NowSeconds, "❤", action: "unreact"))).StatusCode);
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal((1L, "instagram", "ignored"), Assert.Single(f.Recorder.Measurements));
    }

    [Fact]
    public async Task AnInstagramEventForAnUnknownAccountCountsAsUnknownRoute()
    {
        await using var database = await StartDatabaseAsync();
        var f = Watch(new QepApiFactory(database.GetConnectionString()));
        using var _ = f.Factory;
        using var __ = f.Recorder;

        Assert.Equal(HttpStatusCode.OK, (await PostInstagramWebhookAsync(f.Anonymous, InstagramPayloads.Text("m1", NowSeconds, "hola"))).StatusCode);
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal((1L, "instagram", "unknown_route"), Assert.Single(f.Recorder.Measurements));
    }
}
