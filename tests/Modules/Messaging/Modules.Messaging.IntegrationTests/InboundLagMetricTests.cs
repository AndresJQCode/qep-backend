using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Rezago del entrante: segundos entre el <c>timestamp</c> de Meta y el momento en que quedó guardado. Sólo el entrante
/// nuevo mide: un reenvío de Meta no, y un timestamp a futuro (reloj desfasado) cuenta cero, nunca negativo.</summary>
public sealed class InboundLagMetricTests
{
    private const string InstrumentName = "qep.messaging.inbound.lag";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static long NowSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Las mediciones del host de la prueba: el filtro por <c>Meter.Scope</c> evita que las pruebas en paralelo se crucen.</summary>
    private sealed class LagRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(double Value, string? Channel)> _measurements = [];

        public LagRecorder(IMeterFactory factory)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == InstrumentName && ReferenceEquals(instrument.Meter.Scope, factory))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            {
                string? channel = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "channel")
                    {
                        channel = tag.Value as string;
                    }
                }

                lock (_measurements)
                {
                    _measurements.Add((value, channel));
                }
            });
            _listener.Start();
        }

        public IReadOnlyList<(double Value, string? Channel)> Measurements
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

    private sealed record Fixture(QepApiFactory Factory, HttpClient Anonymous, LagRecorder Recorder);

    private static async Task<Fixture> ArrangeWhatsAppAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        return new Fixture(factory, factory.CreateClient(), new LagRecorder(factory.Services.GetRequiredService<IMeterFactory>()));
    }

    private static async Task<Fixture> ArrangeInstagramAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedInstagramConnectionAsync(factory, tenant.TenantId, "Instagram Ventas", InstagramPayloads.DefaultAccountId);
        factory.MetaHandler.Respond($@"^/v24\.0/{InstagramPayloads.DefaultUserId}\?", HttpStatusCode.OK, """{"name":"Laura Pérez","username":"laura.perez","id":"1234567890123456"}""");
        return new Fixture(factory, factory.CreateClient(), new LagRecorder(factory.Services.GetRequiredService<IMeterFactory>()));
    }

    private static async Task IngestAsync(Fixture f, string text, string wamid, long timestamp)
    {
        await PostWebhookAsync(f.Anonymous, MetaPayloads.Inbound("111", "CO.1349120865530274", "573001234567", wamid, timestamp, text));
        await DrainDeliveriesAsync(f.Factory);
    }

    [Fact]
    public async Task ANewInboundRecordsItsLag()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeWhatsAppAsync(database);
        using var _ = f.Factory;
        using var __ = f.Recorder;

        await IngestAsync(f, "hola", "wamid.1", NowSeconds - 30);

        var measurement = Assert.Single(f.Recorder.Measurements);
        Assert.True(measurement.Value >= 29, $"lag was {measurement.Value}");
        Assert.Equal("whatsapp", measurement.Channel);
    }

    [Fact]
    public async Task AMetaRetryDoesNotRecordAgain()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeWhatsAppAsync(database);
        using var _ = f.Factory;
        using var __ = f.Recorder;
        var timestamp = NowSeconds - 30;

        await IngestAsync(f, "hola", "wamid.1", timestamp);
        // Mismo wamid con otro cuerpo: body_sha256 no frena el reenvío, lo frena la ingesta.
        await IngestAsync(f, "hola otra vez", "wamid.1", timestamp);

        Assert.Single(f.Recorder.Measurements);
    }

    [Fact]
    public async Task AFutureTimestampRecordsZero()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeWhatsAppAsync(database);
        using var _ = f.Factory;
        using var __ = f.Recorder;

        await IngestAsync(f, "hola", "wamid.1", NowSeconds + 60);

        Assert.Equal(0d, Assert.Single(f.Recorder.Measurements).Value);
    }

    [Fact]
    public async Task InstagramRecordsWithItsChannel()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeInstagramAsync(database);
        using var _ = f.Factory;
        using var __ = f.Recorder;

        Assert.Equal(HttpStatusCode.OK, (await PostInstagramWebhookAsync(f.Anonymous, InstagramPayloads.Text("m1", NowSeconds - 30, "hola"))).StatusCode);
        await DrainDeliveriesAsync(f.Factory);

        var measurement = Assert.Single(f.Recorder.Measurements);
        Assert.True(measurement.Value >= 29, $"lag was {measurement.Value}");
        Assert.Equal("instagram", measurement.Channel);
    }
}
