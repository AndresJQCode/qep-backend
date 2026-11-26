using System.Diagnostics;
using System.Diagnostics.Metrics;
using Bootstrapper.Valkey;
using BuildingBlocks.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// Los timbres de las colas (spec 2026-11-13, D-7; plan P8 y P9): un productor timbra <c>qep:wake:{cola}</c> y el worker que
/// espera vuelve antes de su red de seguridad. Sin conexión, o con el área apagada (el nulo), el worker espera su intervalo de
/// hoy. Cada prueba usa una cola propia o un oyente nuevo: el contenedor es uno para la clase.
/// </summary>
public sealed class QueueDoorbellTests(ValkeyPlainServer server) : IClassFixture<ValkeyPlainServer>
{
    [Fact]
    public async Task ARingWakesAWaitingWorkerBeforeTheSafetyNet()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        using var listener = harness.Doorbell.Listen(DoorbellQueues.Automation);
        await harness.WaitSubscribedAsync(listener);
        // Fix round 2: conectado y suscrito, el oyente está vivo y el bucle puede repetir un lote lleno sin esperar.
        Assert.True(listener.IsLive);
        var stopwatch = Stopwatch.StartNew();

        var waiting = listener.WaitAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), Ct);
        await Task.Delay(100, Ct);
        harness.Doorbell.Ring(DoorbellQueues.Automation);

        Assert.True(await waiting);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Tardó {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task WithoutARingTheWaitReturnsAtTheSafetyNet()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        using var listener = harness.Doorbell.Listen(DoorbellQueues.Export);
        await harness.WaitSubscribedAsync(listener);
        var stopwatch = Stopwatch.StartNew();

        var rang = await listener.WaitAsync(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(300), Ct);

        Assert.False(rang);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(290), TimeSpan.FromSeconds(1));
    }

    // Review Focus 4: el worker está drenando (no espera) cuando llega el aviso. El siguiente WaitAsync vuelve al instante.
    [Fact]
    public async Task ARingDuringTheDrainIsNotLost()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        using var listener = harness.Doorbell.Listen(DoorbellQueues.Media);
        await harness.WaitSubscribedAsync(listener);

        harness.Doorbell.Ring(DoorbellQueues.Media);
        await Task.Delay(300, Ct);
        var stopwatch = Stopwatch.StartNew();

        Assert.True(await listener.WaitAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), Ct));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(200), $"Tardó {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task ManyRingsCountAsOne()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        using var listener = harness.Doorbell.Listen(DoorbellQueues.Scheduled);
        await harness.WaitSubscribedAsync(listener);

        for (var i = 0; i < 5; i++)
        {
            harness.Doorbell.Ring(DoorbellQueues.Scheduled);
        }

        await Task.Delay(300, Ct);
        Assert.True(await listener.WaitAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), Ct));
        var stopwatch = Stopwatch.StartNew();
        Assert.False(await listener.WaitAsync(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(300), Ct));
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(290), $"Volvió a los {stopwatch.Elapsed}.");
    }

    // Review Focus 4: outbox tiene una decena de consumidores en el mismo proceso; un aviso los despierta a todos.
    [Fact]
    public async Task OneRingWakesEveryListenerOfTheQueue()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        using var first = harness.Doorbell.Listen(DoorbellQueues.Outbox);
        using var second = harness.Doorbell.Listen(DoorbellQueues.Outbox);
        using var third = harness.Doorbell.Listen(DoorbellQueues.Outbox);
        await harness.WaitSubscribedAsync(first);

        var waits = new[] { first, second, third }
            .Select(listener => listener.WaitAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), Ct))
            .ToArray();
        await Task.Delay(100, Ct);
        harness.Doorbell.Ring(DoorbellQueues.Outbox);

        Assert.All(await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(5), Ct), Assert.True);
    }

    [Fact]
    public async Task ARingFromAnotherProcessWakesTheListener()
    {
        await using var producer = await Harness.StartAsync(ConnectionString());
        await using var consumer = await Harness.StartAsync(ConnectionString());
        using var listener = consumer.Doorbell.Listen(DoorbellQueues.Webhook);
        await consumer.WaitSubscribedAsync(listener);

        var waiting = listener.WaitAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), Ct);
        await Task.Delay(100, Ct);
        producer.Doorbell.Ring(DoorbellQueues.Webhook);

        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    // Contenedor propio: detenerlo dejaría sin Valkey al resto de las pruebas de la clase.
    [Fact]
    public async Task WithValkeyDownWaitIsTheTodaysPoll()
    {
        await using var container = await ValkeyTestServer.StartAsync(withProductionAcl: false, Ct);
        await using var harness = await Harness.StartAsync(ValkeyTestServer.ConnectionString(container, asQep: false));
        using var listener = harness.Doorbell.Listen(DoorbellQueues.Campaign);
        await harness.WaitSubscribedAsync(listener);
        await container.StopAsync(Ct);
        await harness.WaitDisconnectedAsync();
        Assert.False(listener.IsLive);
        var stopwatch = Stopwatch.StartNew();

        var rang = await listener.WaitAsync(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(30), Ct);

        Assert.False(rang);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(290), TimeSpan.FromSeconds(2));
    }

    // Review Focus 5: con el área apagada el bot sigue reclamando cada 1 s, no cada 15.
    [Fact]
    public async Task WithTheAreaOffTheListenerWaitsTodaysInterval()
    {
        var doorbell = new NullQueueDoorbell();
        using var listener = doorbell.Listen(DoorbellQueues.Automation);
        doorbell.Ring(DoorbellQueues.Automation);
        Assert.False(listener.IsLive);
        var stopwatch = Stopwatch.StartNew();

        var rang = await listener.WaitAsync(TimeSpan.FromSeconds(1), DoorbellQueues.SafetyNet(DoorbellQueues.Automation), Ct);

        Assert.False(rang);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(990), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task TheChannelIsPrefixed()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.Multiplexer.GetSubscriber().SubscribeAsync(
            RedisChannel.Literal("qep:wake:automation"), (_, _) => received.TrySetResult());

        harness.Doorbell.Ring(DoorbellQueues.Automation);

        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public void EveryQueueHasItsSafetyNet()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), DoorbellQueues.SafetyNet(DoorbellQueues.Automation));
        Assert.Equal(TimeSpan.FromSeconds(15), DoorbellQueues.SafetyNet(DoorbellQueues.Webhook));
        Assert.Equal(TimeSpan.FromSeconds(30), DoorbellQueues.SafetyNet(DoorbellQueues.ShopifyWebhook));
        Assert.Equal(TimeSpan.FromSeconds(30), DoorbellQueues.SafetyNet(DoorbellQueues.Media));
        Assert.Equal(TimeSpan.FromSeconds(5), DoorbellQueues.SafetyNet(DoorbellQueues.Scheduled));
        Assert.Equal(TimeSpan.FromSeconds(30), DoorbellQueues.SafetyNet(DoorbellQueues.Campaign));
        Assert.Equal(TimeSpan.FromSeconds(30), DoorbellQueues.SafetyNet(DoorbellQueues.Export));
        Assert.Equal(TimeSpan.FromSeconds(30), DoorbellQueues.SafetyNet(DoorbellQueues.Outbox));
    }

    // El bucle con el área prendida y el oyente vivo: un tick que trae lote lleno se repite sin esperar; uno que no, espera con el oyente.
    [Fact]
    public async Task PollingRepeatsAFullBatchWithoutWaitingAndWaitsOtherwise()
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var listener = new CountingListener(() => stopping.Cancel(), live: true);
        var results = new Queue<bool>([true, true, false]);
        var ticks = 0;

        await DoorbellPolling.RunAsync(
            listener,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(15),
            _ =>
            {
                ticks++;
                return Task.FromResult(results.Dequeue());
            },
            stopping.Token);

        Assert.Equal(3, ticks);
        Assert.Equal(1, listener.Waits);
    }

    // Review de bloque B (P11): un tick que conoce su próximo vencimiento espera min(red de seguridad, vencimiento); el intervalo de
    // hoy (área apagada) no cambia.
    [Fact]
    public async Task AKnownNextDueShortensTheSafetyNetButNotThePoll()
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var listener = new RecordingListener(() => stopping.Cancel());

        await DoorbellPolling.RunAsync(
            listener,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            _ => Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(12)),
            stopping.Token);

        var (poll, safetyNet) = Assert.Single(listener.Waits);
        Assert.Equal(TimeSpan.FromSeconds(12), safetyNet);
        Assert.InRange(poll, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ADueLongerThanTheSafetyNetKeepsTheSafetyNet()
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var listener = new RecordingListener(() => stopping.Cancel());

        await DoorbellPolling.RunAsync(
            listener, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), _ => Task.FromResult<TimeSpan?>(TimeSpan.FromMinutes(5)), stopping.Token);

        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(listener.Waits).SafetyNet);
    }

    // Fix round 2 (b): área prendida y oyente vivo ⇒ un lote lleno repite sin esperar.
    [Fact]
    public async Task AZeroDueRepeatsWithoutWaiting()
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var listener = new RecordingListener(() => stopping.Cancel(), live: true);
        var dues = new Queue<TimeSpan?>([TimeSpan.Zero, TimeSpan.Zero, null]);

        await DoorbellPolling.RunAsync(
            listener, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15), _ => Task.FromResult(dues.Dequeue()), stopping.Token);

        Assert.Empty(dues);
        Assert.Equal(TimeSpan.FromSeconds(15), Assert.Single(listener.Waits).SafetyNet);
    }

    // Fix round 2 (a), regla del controlador: con el área apagada, Valkey caído o el oyente sin suscribir, un lote lleno NO repite al
    // instante: espera el intervalo de hoy menos lo que tardó el tick, como el PeriodicTimer. Un consumidor de correo con el proveedor
    // caído quemaría el outbox entero a Failed en segundos.
    [Fact]
    public async Task WithTheListenerNotLiveAFullBatchWaitsThePollMinusTheTick()
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var listener = new RecordingListener(() => stopping.Cancel(), live: false);
        var ticks = 0;

        await DoorbellPolling.RunAsync(
            listener,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(15),
            async _ =>
            {
                ticks++;
                await Task.Delay(100, Ct);
                return true;
            },
            stopping.Token);

        Assert.Equal(1, ticks);
        Assert.InRange(Assert.Single(listener.Waits).Poll, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(410));
    }

    [Fact]
    public async Task WithTheListenerNotLiveAZeroDueWaitsThePoll()
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var listener = new RecordingListener(() => stopping.Cancel(), live: false);
        var dues = new Queue<TimeSpan?>([TimeSpan.Zero, TimeSpan.Zero]);

        await DoorbellPolling.RunAsync(
            listener, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15), _ => Task.FromResult(dues.Dequeue()), stopping.Token);

        Assert.Single(dues);
        Assert.InRange(Assert.Single(listener.Waits).Poll, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(1));
    }

    // Review de bloque B: con el área apagada la cadencia es la de hoy (PeriodicTimer): lo que tardó el tick se descuenta del intervalo,
    // y un tick más largo que el intervalo espera cero.
    [Fact]
    public async Task ThePollIsShortenedByTheTickDuration()
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var listener = new RecordingListener(() => stopping.Cancel());

        await DoorbellPolling.RunAsync(
            listener,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(15),
            async _ =>
            {
                await Task.Delay(300, Ct);
                return false;
            },
            stopping.Token);

        Assert.InRange(Assert.Single(listener.Waits).Poll, TimeSpan.Zero, TimeSpan.FromMilliseconds(210));
    }

    [Fact]
    public async Task ATickLongerThanThePollWaitsZero()
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var listener = new RecordingListener(() => stopping.Cancel());

        await DoorbellPolling.RunAsync(
            listener,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(15),
            async _ =>
            {
                await Task.Delay(250, Ct);
                return false;
            },
            stopping.Token);

        Assert.Equal(TimeSpan.Zero, Assert.Single(listener.Waits).Poll);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ConnectionString() => ValkeyTestServer.ConnectionString(server.Container, asQep: false);

    private sealed class RecordingListener(Action onWait, bool live = false) : IDoorbellListener
    {
        public List<(TimeSpan Poll, TimeSpan SafetyNet)> Waits { get; } = [];

        public bool IsLive => live;

        public Task<bool> WaitAsync(TimeSpan poll, TimeSpan safetyNet, CancellationToken cancellationToken)
        {
            Waits.Add((poll, safetyNet));
            onWait();
            return Task.FromCanceled<bool>(cancellationToken);
        }

        public void Dispose()
        {
        }
    }

    private sealed class CountingListener(Action onWait, bool live = false) : IDoorbellListener
    {
        public int Waits { get; private set; }

        public bool IsLive => live;

        public Task<bool> WaitAsync(TimeSpan poll, TimeSpan safetyNet, CancellationToken cancellationToken)
        {
            Waits++;
            onWait();
            return Task.FromCanceled<bool>(cancellationToken);
        }

        public void Dispose()
        {
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ValkeyConnection _connection;

        private Harness(ServiceProvider provider, ValkeyConnection connection, ValkeyQueueDoorbell doorbell)
        {
            _provider = provider;
            _connection = connection;
            Doorbell = doorbell;
        }

        public ValkeyQueueDoorbell Doorbell { get; }

        public IConnectionMultiplexer Multiplexer => _connection.Multiplexer!;

        public static async Task<Harness> StartAsync(string connectionString)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Valkey"] = connectionString })
                .Build();
            var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var factory = provider.GetRequiredService<IMeterFactory>();
            var options = Options.Create(new ValkeyOptions { Enabled = true, Areas = { Doorbells = true } });
            var connection = new ValkeyConnection(configuration, options, NullLogger<ValkeyConnection>.Instance);
            var gateway = new ValkeyGateway(
                connection, options, new ValkeyMetrics(factory, connection), NullLogger<ValkeyGateway>.Instance);
            await connection.StartAsync(Ct);
            return new Harness(provider, connection, new ValkeyQueueDoorbell(gateway));
        }

        /// <summary>La suscripción sale con el primer <c>Listen</c>, en segundo plano; la prueba espera a que esté.</summary>
        public async Task WaitSubscribedAsync(IDoorbellListener listener)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!Doorbell.IsSubscribed(((ValkeyQueueDoorbell.Listener)listener).Queue) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, Ct);
            }

            Assert.True(Doorbell.IsSubscribed(((ValkeyQueueDoorbell.Listener)listener).Queue));
        }

        public async Task WaitDisconnectedAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (_connection.Multiplexer?.IsConnected == true && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, Ct);
            }

            Assert.False(_connection.Multiplexer?.IsConnected == true);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }
}
