using System.Diagnostics.Metrics;
using Bootstrapper.Observability;
using Bootstrapper.UnitTests.Valkey;
using Bootstrapper.Valkey;
using BuildingBlocks.Application;
using BuildingBlocks.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bootstrapper.UnitTests;

public sealed class QueueSamplerWorkerTests(ValkeyPlainServer server) : IClassFixture<ValkeyPlainServer>
{
    private sealed class FixedNowClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class FakeSource(string? failingQueue) : IQueueMetricsSource
    {
        public Task<QueueReading> ReadAsync(string queue, CancellationToken cancellationToken)
        {
            if (queue == failingQueue)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(new QueueReading(queue, 5, 1, queue == QueueNames.Campaign ? null : 3));
        }
    }

    [Fact]
    public async Task OneFailingQueueDoesNotStopTheOthers()
    {
        using var provider = new ServiceCollection()
            .AddMetrics()
            .AddScoped<IQueueMetricsSource>(_ => new FakeSource(QueueNames.Media))
            .BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        var gauges = new QueueGauges(factory, new FixedNowClock());
        var worker = new QueueSamplerWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), gauges, new WebhookArrivalGauge(factory, new FixedNowClock()),
            LeaderHarness.WithValkeyOff(), NullLogger<QueueSamplerWorker>.Instance);

        var queues = new HashSet<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Meter.Name == QepObservability.MeterName
                && instrument.Name == "qep.queue.backlog")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "queue")
                {
                    lock (queues)
                    {
                        queues.Add((string)tag.Value!);
                    }
                }
            }
        });
        listener.Start();

        await worker.SampleAsync(TestContext.Current.CancellationToken);
        listener.RecordObservableInstruments();

        Assert.Equal(
            QueueNames.All.Where(q => q != QueueNames.Media).Order(),
            queues.Order());
    }

    // Spec 2026-11-13 (Valkey), D-7; plan P14: con el área de timbres prendida, un solo pod muestrea por vuelta.
    [Fact]
    public async Task OnlyTheLeaderSamplesTheQueues()
    {
        var connectionString = ValkeyTestServer.ConnectionString(server.Container, asQep: false);
        await using var first = await LeaderHarness.StartAsync(connectionString, doorbells: true);
        await using var second = await LeaderHarness.StartAsync(connectionString, doorbells: true);
        await first.ClearLeaderAsync();
        var source = new CountingSource();

        await NewWorker(source, first.Leadership).RunOnceAsync(TestContext.Current.CancellationToken);
        await NewWorker(source, second.Leadership).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(QueueNames.All.Count, source.Reads);
    }

    // El líder sigue siéndolo en su vuelta siguiente: su clave dura lo mismo que el intervalo, y no puede perderla por
    // llegar justo cuando todavía existe.
    [Fact]
    public async Task TheLeaderKeepsLeadingOnItsNextRound()
    {
        var connectionString = ValkeyTestServer.ConnectionString(server.Container, asQep: false);
        await using var first = await LeaderHarness.StartAsync(connectionString, doorbells: true);
        await using var second = await LeaderHarness.StartAsync(connectionString, doorbells: true);
        await first.ClearLeaderAsync();
        var ct = TestContext.Current.CancellationToken;

        Assert.True(await first.Leadership.TryLeadAsync(ct));
        Assert.False(await second.Leadership.TryLeadAsync(ct));
        Assert.True(await first.Leadership.TryLeadAsync(ct));
    }

    [Fact]
    public async Task WithTheAreaOffBothSample()
    {
        var connectionString = ValkeyTestServer.ConnectionString(server.Container, asQep: false);
        await using var first = await LeaderHarness.StartAsync(connectionString, doorbells: false);
        await using var second = await LeaderHarness.StartAsync(connectionString, doorbells: false);
        var source = new CountingSource();

        await NewWorker(source, first.Leadership).RunOnceAsync(TestContext.Current.CancellationToken);
        await NewWorker(source, second.Leadership).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2 * QueueNames.All.Count, source.Reads);
    }

    [Fact]
    public async Task WithValkeyDownBothSample()
    {
        await using var first = await LeaderHarness.StartAsync("127.0.0.1:1,connectTimeout=500", doorbells: true);
        await using var second = await LeaderHarness.StartAsync("127.0.0.1:1,connectTimeout=500", doorbells: true);
        var source = new CountingSource();

        await NewWorker(source, first.Leadership).RunOnceAsync(TestContext.Current.CancellationToken);
        await NewWorker(source, second.Leadership).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2 * QueueNames.All.Count, source.Reads);
    }

    private static QueueSamplerWorker NewWorker(CountingSource source, QueueSamplerLeadership leadership)
    {
        var provider = new ServiceCollection()
            .AddMetrics()
            .AddScoped<IQueueMetricsSource>(_ => source)
            .BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        var gauges = new QueueGauges(factory, new FixedNowClock());
        return new QueueSamplerWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), gauges, new WebhookArrivalGauge(factory, new FixedNowClock()), leadership,
            NullLogger<QueueSamplerWorker>.Instance);
    }

    // Spec 2026-11-16: la hora del último webhook sale en la misma vuelta que las colas.
    [Fact]
    public async Task TheLastWebhookIsRecordedInTheSameRound()
    {
        var receivedAt = new DateTimeOffset(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);
        using var provider = new ServiceCollection()
            .AddMetrics()
            .AddScoped<IQueueMetricsSource>(_ => new FakeSource(null))
            .AddScoped<IWebhookArrivalSource>(_ => new FixedArrivals(receivedAt))
            .BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        var worker = new QueueSamplerWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), new QueueGauges(factory, new FixedNowClock()),
            new WebhookArrivalGauge(factory, new FixedNowClock()), LeaderHarness.WithValkeyOff(), NullLogger<QueueSamplerWorker>.Instance);

        var values = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Name == "qep.webhook.last_received")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) =>
        {
            lock (values)
            {
                values.Add(value);
            }
        });
        listener.Start();

        await worker.SampleAsync(TestContext.Current.CancellationToken);
        listener.RecordObservableInstruments();

        Assert.Equal(receivedAt.ToUnixTimeSeconds(), Assert.Single(values));
    }

    private sealed class FixedArrivals(DateTimeOffset receivedAt) : IWebhookArrivalSource
    {
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> ReadLastReceivedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(new Dictionary<string, DateTimeOffset> { ["whatsapp"] = receivedAt });
    }

    private sealed class CountingSource : IQueueMetricsSource
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public Task<QueueReading> ReadAsync(string queue, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult(new QueueReading(queue, 0, 0, null));
        }
    }

    /// <summary>Un gateway propio por «pod»: dos instancias del muestreador sobre el mismo Valkey.</summary>
    private sealed class LeaderHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ValkeyConnection _connection;

        private LeaderHarness(ServiceProvider provider, ValkeyConnection connection, QueueSamplerLeadership leadership)
        {
            _provider = provider;
            _connection = connection;
            Leadership = leadership;
        }

        public QueueSamplerLeadership Leadership { get; }

        /// <summary>Sin Valkey (<c>Valkey:Enabled = false</c>): siempre lidera, como hoy.</summary>
        public static QueueSamplerLeadership WithValkeyOff()
        {
            var options = Options.Create(new ValkeyOptions());
            var connection = new ValkeyConnection(
                new ConfigurationBuilder().Build(), options, NullLogger<ValkeyConnection>.Instance);
            var factory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
            return new QueueSamplerLeadership(new ValkeyGateway(
                connection, options, new ValkeyMetrics(factory, connection), NullLogger<ValkeyGateway>.Instance));
        }

        public static async Task<LeaderHarness> StartAsync(string connectionString, bool doorbells)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Valkey"] = connectionString })
                .Build();
            var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var factory = provider.GetRequiredService<IMeterFactory>();
            var options = Options.Create(new ValkeyOptions { Enabled = true, Areas = { Doorbells = doorbells } });
            var connection = new ValkeyConnection(configuration, options, NullLogger<ValkeyConnection>.Instance);
            var gateway = new ValkeyGateway(
                connection, options, new ValkeyMetrics(factory, connection), NullLogger<ValkeyGateway>.Instance);
            await connection.StartAsync(TestContext.Current.CancellationToken);
            return new LeaderHarness(provider, connection, new QueueSamplerLeadership(gateway));
        }

        // La clave del líder es una sola para todo el contenedor de la clase: cada prueba arranca sin líder.
        public async Task ClearLeaderAsync() =>
            await _connection.Multiplexer!.GetDatabase().KeyDeleteAsync(QueueSamplerLeadership.Key);

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }
}
