using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;
using Bootstrapper.Valkey;
using Bootstrapper.Valkey.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// La fábrica de particiones de las políticas de ventana fija (spec 2026-11-13, D-3): con <c>Valkey:Areas:RateLimiting</c>
/// apagado devuelve exactamente el limitador de memoria de antes (V3 del plan); prendido, el de Valkey. Las particiones con
/// patrón de ruta entran a la clave con un hash corto (V7).
/// </summary>
public sealed class SharedRateLimitPartitionsTests(ValkeyPlainServer server) : IClassFixture<ValkeyPlainServer>
{
    private const string RoutePartition = "/api/v1/auth/login|203.0.113.7";

    [Fact]
    public async Task WithTheAreaOffThePartitionIsTheInMemoryLimiter()
    {
        await using var node = await Node.StartAsync(PlainConnectionString(), rateLimiting: false, TimeProvider.System);

        var partition = node.Partitions.FixedWindow("public", "203.0.113.7", TenPerMinute());

        using var limiter = partition.Factory(partition.PartitionKey);
        Assert.IsType<FixedWindowRateLimiter>(limiter);
    }

    [Fact]
    public async Task WithTheAreaOnThePartitionIsTheValkeyLimiter()
    {
        await using var node = await Node.StartAsync(PlainConnectionString(), rateLimiting: true, TimeProvider.System);

        var partition = node.Partitions.FixedWindow("public", "203.0.113.7", TenPerMinute());

        using var limiter = partition.Factory(partition.PartitionKey);
        Assert.IsType<ValkeyFixedWindowRateLimiter>(limiter);
    }

    [Fact]
    public async Task ARoutePartitionIsHashedInTheKey()
    {
        var clock = new FixedClock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 * 60));
        await using var node = await Node.StartAsync(PlainConnectionString(), rateLimiting: true, clock);
        var policy = "test-" + Guid.NewGuid().ToString("N");

        var partition = node.Partitions.FixedWindow(policy, RoutePartition, TenPerMinute(), hashPartition: true);
        using var limiter = partition.Factory(partition.PartitionKey);
        using (var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken))
        {
            Assert.True(lease.IsAcquired);
        }

        var keys = node.KeysOf($"qep:rl:{policy}:*");
        var key = Assert.Single(keys);
        Assert.Equal(
            (string?)ValkeyKeys.RateLimit(policy, ValkeyKeys.ShortHash(RoutePartition), clock.GetUtcNow().ToUnixTimeSeconds()),
            key);
        Assert.DoesNotContain("/api/", key, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.7", key, StringComparison.Ordinal);
    }

    private static FixedWindowRateLimiterOptions TenPerMinute() => new()
    {
        PermitLimit = 10,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };

    private string PlainConnectionString() => ValkeyTestServer.ConnectionString(server.Container, asQep: false);

    private sealed class Node : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ValkeyConnection _connection;

        private Node(ServiceProvider provider, ValkeyConnection connection, SharedRateLimitPartitions partitions)
        {
            _provider = provider;
            _connection = connection;
            Partitions = partitions;
        }

        public SharedRateLimitPartitions Partitions { get; }

        public static async Task<Node> StartAsync(string connectionString, bool rateLimiting, TimeProvider clock)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Valkey"] = connectionString })
                .Build();
            var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var factory = provider.GetRequiredService<IMeterFactory>();
            var options = Options.Create(new ValkeyOptions { Enabled = true, Areas = { RateLimiting = rateLimiting } });
            var connection = new ValkeyConnection(configuration, options, NullLogger<ValkeyConnection>.Instance);
            var gateway = new ValkeyGateway(
                connection, options, new ValkeyMetrics(factory, connection), NullLogger<ValkeyGateway>.Instance);
            await connection.StartAsync(TestContext.Current.CancellationToken);
            return new Node(provider, connection, new SharedRateLimitPartitions(gateway, clock));
        }

        public IReadOnlyList<string> KeysOf(string pattern)
        {
            var multiplexer = _connection.Multiplexer!;
            var valkey = multiplexer.GetServer(multiplexer.GetEndPoints()[0]);
            return [.. valkey.Keys(pattern: pattern).Select(key => key.ToString())];
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
