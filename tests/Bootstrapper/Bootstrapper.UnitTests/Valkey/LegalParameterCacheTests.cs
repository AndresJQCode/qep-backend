using Bootstrapper.Valkey;
using Bootstrapper.Valkey.LegalParameters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Hr.Application;
using Modules.Hr.Domain;
using Modules.Hr.Infrastructure;
using StackExchange.Redis;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// La caché de valores legales (spec 2026-11-22, «Caché»): el decorador de <c>ILegalParameterSource</c> con la tabla entera bajo una
/// clave versionada (<c>qep:legalparams:{ver}</c>), la versión leída <b>antes</b> de cargar y subida <b>después</b> del commit. Con el
/// área apagada, la copia del pod con TTL de 60 s. Las pruebas de Valkey usan contenedor real y una tabla propia por prueba.
/// </summary>
public sealed class LegalParameterCacheTests(ValkeyPlainServer server) : IClassFixture<ValkeyPlainServer>
{
    private static readonly DateOnly January2026 = new(2026, 1, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LegalParameterValue Value(decimal amount) => new(LegalParameterKey.Smmlv, January2026, amount);

    private static LegalParameterBracket Bracket() =>
        new(LegalBracketKey.SolidarityFund, January2026, 4m, 16m, 0.01m);

    // Una base de mentira: cuenta cuántas veces se leyó y puede retener la lectura para armar la carrera.
    private sealed class CountingSource : ILegalParameterSource
    {
        private int _loads;
        private TaskCompletionSource? _gate;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public decimal Smmlv { get; set; } = 1_750_905m;

        public int Loads => Volatile.Read(ref _loads);

        public Task Started => _started.Task;

        /// <summary>La próxima lectura se detiene después de leer los valores y espera a <see cref="Release"/>.</summary>
        public void HoldNextLoad() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate!.SetResult();

        public async Task<IReadOnlyList<LegalParameterValue>> ListAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _loads);
            var values = new List<LegalParameterValue> { Value(Smmlv) };
            if (_gate is { } gate)
            {
                _started.TrySetResult();
                await gate.Task;
            }

            return values;
        }

        public Task<IReadOnlyList<LegalParameterBracket>> ListBracketsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LegalParameterBracket>>([Bracket()]);
    }

    // ---- Registro ----

    private static (ServiceProvider Provider, ServiceCollection Services) RegisterReal(string areaOn)
    {
        var services = ValkeyRegistrationTests.Register(
            ("Valkey:Areas:LegalParameters", areaOn),
            ("ConnectionStrings:QepDatabase", "Host=localhost;Database=unused;Username=unused;Password=unused"));
        var configuration = (IConfiguration)services.Single(descriptor => descriptor.ServiceType == typeof(IConfiguration)).ImplementationInstance!;

        // El orden es la trampa: AddHrInfrastructure registra el ILegalParameterSource que el decorador envuelve.
        services.AddHrInfrastructure(configuration);
        services.AddLegalParameterCache(configuration);
        return (services.BuildServiceProvider(), services);
    }

    [Theory]
    [InlineData("false", typeof(MemoryLegalParameterSource), typeof(MemoryLegalParameterCacheVersion))]
    [InlineData("true", typeof(ValkeyLegalParameterSource), typeof(ValkeyLegalParameterCacheVersion))]
    public void ResolvingTheSourceGivesTheDecorator(string areaOn, Type source, Type version)
    {
        var (provider, _) = RegisterReal(areaOn);
        using var _ = provider;
        using var scope = provider.CreateScope();

        Assert.IsType(source, scope.ServiceProvider.GetRequiredService<ILegalParameterSource>());
        Assert.IsType(version, provider.GetRequiredService<ILegalParameterCacheVersion>());
    }

    [Fact]
    public void TheDecoratorReplacesTheHrRegistrationInsteadOfAddingOne()
    {
        var (provider, services) = RegisterReal("false");
        using var _ = provider;

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ILegalParameterSource));
    }

    [Fact]
    public void TheMemoryCacheIsOneSingletonSharedByTheSourceAndTheVersion()
    {
        var (provider, services) = RegisterReal("false");
        using var _ = provider;

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(MemoryLegalParameterCache));
    }

    [Fact]
    public void TagOfLegalParametersIsStable() =>
        Assert.Equal("legal_parameters", ValkeyAreas.TagOf(ValkeyArea.LegalParameters));

    [Fact]
    public void TheAreaFollowsItsSwitchOnlyWithTheGeneralOne()
    {
        Assert.True(ValkeyAreas.IsOn(new ValkeyAreaOptions { LegalParameters = true }, ValkeyArea.LegalParameters));
        Assert.False(ValkeyAreas.IsOn(new ValkeyAreaOptions(), ValkeyArea.LegalParameters));
    }

    [Fact]
    public void TheKeysUseTheQepPrefix()
    {
        Assert.Equal("qep:legalparams:ver", (string?)ValkeyKeys.LegalParametersVersion);
        Assert.Equal("qep:legalparams:42", (string?)ValkeyKeys.LegalParametersTable(42));
    }

    [Fact]
    public void ASnapshotRoundTripsThroughItsJson()
    {
        var snapshot = new LegalParameterSnapshot(
            [Value(1_750_905m), new LegalParameterValue(LegalParameterKey.ArlClassIRate, new DateOnly(2025, 6, 25), 0.00522m)],
            [Bracket(), new LegalParameterBracket(LegalBracketKey.SolidarityFund, January2026, 16m, null, 0.02m)]);

        var read = LegalParameterSnapshot.Deserialize(snapshot.Serialize());

        Assert.Equal(
            snapshot.Values.Select(v => (v.Key, v.EffectiveFrom, v.Value)),
            read.Values.Select(v => (v.Key, v.EffectiveFrom, v.Value)));
        Assert.Equal(
            snapshot.Brackets.Select(b => (b.Key, b.EffectiveFrom, b.LowerBound, b.UpperBound, b.Rate)),
            read.Brackets.Select(b => (b.Key, b.EffectiveFrom, b.LowerBound, b.UpperBound, b.Rate)));
    }

    // ---- Memoria del pod (área apagada) ----

    private static (MemoryLegalParameterCache Cache, ManualTimeProvider Time) NewMemory()
    {
        var time = new ManualTimeProvider();
        return (new MemoryLegalParameterCache(time), time);
    }

    [Fact]
    public async Task ServesFromMemoryWithinSixtySeconds()
    {
        var (cache, time) = NewMemory();
        var inner = new CountingSource();

        var first = await new MemoryLegalParameterSource(inner, cache).ListAsync(Ct);
        time.Advance(TimeSpan.FromSeconds(59));
        var second = await new MemoryLegalParameterSource(inner, cache).ListAsync(Ct);
        var brackets = await new MemoryLegalParameterSource(inner, cache).ListBracketsAsync(Ct);

        Assert.Equal(1, inner.Loads);
        Assert.Equal(1_750_905m, Assert.Single(first).Value);
        Assert.Equal(1_750_905m, Assert.Single(second).Value);
        Assert.Equal(0.01m, Assert.Single(brackets).Rate);
    }

    [Fact]
    public async Task ExpiresAfterSixtySeconds()
    {
        var (cache, time) = NewMemory();
        var inner = new CountingSource();
        await new MemoryLegalParameterSource(inner, cache).ListAsync(Ct);

        time.Advance(TimeSpan.FromSeconds(61));
        inner.Smmlv = 2_000_000m;
        var after = await new MemoryLegalParameterSource(inner, cache).ListAsync(Ct);

        Assert.Equal(2, inner.Loads);
        Assert.Equal(2_000_000m, Assert.Single(after).Value);
    }

    [Fact]
    public async Task ABumpInvalidates()
    {
        var (cache, _) = NewMemory();
        var inner = new CountingSource();
        await new MemoryLegalParameterSource(inner, cache).ListAsync(Ct);

        await new MemoryLegalParameterCacheVersion(cache).BumpAsync(Ct);
        inner.Smmlv = 2_000_000m;
        var after = await new MemoryLegalParameterSource(inner, cache).ListAsync(Ct);

        Assert.Equal(2, inner.Loads);
        Assert.Equal(2_000_000m, Assert.Single(after).Value);
    }

    [Fact]
    public async Task AReadThatStartedBeforeTheBumpIsBornStale()
    {
        var (cache, _) = NewMemory();
        var inner = new CountingSource();
        inner.HoldNextLoad();
        var late = new MemoryLegalParameterSource(inner, cache).ListAsync(Ct);
        await inner.Started;

        // El commit y el bump ocurren mientras la lectura vieja sigue en la base.
        inner.Smmlv = 2_000_000m;
        cache.Bump();
        inner.Release();
        var stale = await late;
        var fresh = await new MemoryLegalParameterSource(inner, cache).ListAsync(Ct);

        Assert.Equal(1_750_905m, Assert.Single(stale).Value);
        Assert.Equal(2, inner.Loads);
        Assert.Equal(2_000_000m, Assert.Single(fresh).Value);
    }

    // ---- Valkey ----

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ValkeyConnection _connection;

        private Harness(
            ServiceProvider provider,
            ValkeyConnection connection,
            ValkeyGateway gateway,
            ValkeySessionCacheTests.FallbackCounter fallbacks)
        {
            _provider = provider;
            _connection = connection;
            Gateway = gateway;
            Fallbacks = fallbacks;
        }

        public ValkeyGateway Gateway { get; }

        public ValkeySessionCacheTests.FallbackCounter Fallbacks { get; }

        public IDatabase Database => _connection.Multiplexer!.GetDatabase();

        /// <param name="connected">Sin conectar, el gateway cuenta un respaldo y devuelve al instante: Valkey caído.</param>
        public static async Task<Harness> StartAsync(string connectionString, bool connected = true)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Valkey"] = connectionString })
                .Build();
            var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var factory = provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
            var options = Options.Create(new ValkeyOptions { Enabled = true, Areas = { LegalParameters = true } });
            var connection = new ValkeyConnection(configuration, options, NullLogger<ValkeyConnection>.Instance);
            var fallbacks = new ValkeySessionCacheTests.FallbackCounter(factory);
            var gateway = new ValkeyGateway(
                connection, options, new ValkeyMetrics(factory, connection), NullLogger<ValkeyGateway>.Instance);
            if (connected)
            {
                await connection.StartAsync(Ct);
            }

            return new Harness(provider, connection, gateway, fallbacks);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            Fallbacks.Dispose();
            await _provider.DisposeAsync();
        }
    }

    // El contenedor es de la clase y la versión y la tabla sobreviven entre pruebas: cada una arranca de una base vacía.
    private async Task<Harness> StartHarnessAsync()
    {
        var harness = await Harness.StartAsync(ValkeyTestServer.ConnectionString(server.Container, asQep: false));
        foreach (var key in harness.Database.Multiplexer.GetServers()[0].Keys(pattern: "qep:legalparams:*"))
        {
            await harness.Database.KeyDeleteAsync(key);
        }

        return harness;
    }

    private static ValkeyLegalParameterSource NewSource(CountingSource inner, Harness harness) =>
        new(inner, harness.Gateway, TimeProvider.System);

    [Fact]
    public async Task SecondReadHitsValkey()
    {
        await using var harness = await StartHarnessAsync();
        var inner = new CountingSource();

        var first = await NewSource(inner, harness).ListAsync(Ct);
        var second = await NewSource(inner, harness).ListAsync(Ct);
        var brackets = await NewSource(inner, harness).ListBracketsAsync(Ct);

        Assert.Equal(1, inner.Loads);
        Assert.Equal(1_750_905m, Assert.Single(first).Value);
        Assert.Equal(1_750_905m, Assert.Single(second).Value);
        Assert.Equal(0.01m, Assert.Single(brackets).Rate);
        Assert.Equal(0, harness.Fallbacks.Count("legal_parameters"));
    }

    [Fact]
    public async Task ASourceReadsTheTableOnceEvenIfAskedTwice()
    {
        await using var harness = await StartHarnessAsync();
        var inner = new CountingSource();
        var source = NewSource(inner, harness);

        await source.ListAsync(Ct);
        await source.ListBracketsAsync(Ct);
        await source.ListAsync(Ct);

        Assert.Equal(1, inner.Loads);
    }

    [Fact]
    public async Task BumpChangesTheKey()
    {
        await using var harness = await StartHarnessAsync();
        var inner = new CountingSource();
        var version = new ValkeyLegalParameterCacheVersion(
            harness.Gateway, TimeProvider.System, NullLogger<ValkeyLegalParameterCacheVersion>.Instance);
        await NewSource(inner, harness).ListAsync(Ct);
        var before = (long)await harness.Database.StringGetAsync(ValkeyKeys.LegalParametersVersion);

        await version.BumpAsync(Ct);
        inner.Smmlv = 2_000_000m;
        var after = await NewSource(inner, harness).ListAsync(Ct);

        var current = (long)await harness.Database.StringGetAsync(ValkeyKeys.LegalParametersVersion);
        Assert.Equal(before + 1, current);
        Assert.Equal(2, inner.Loads);
        Assert.Equal(2_000_000m, Assert.Single(after).Value);
        Assert.True(await harness.Database.KeyExistsAsync(ValkeyKeys.LegalParametersTable(before)));
        Assert.True(await harness.Database.KeyExistsAsync(ValkeyKeys.LegalParametersTable(current)));
    }

    [Fact]
    public async Task ABumpOnAnEmptyServerSeedsAboveTheClock()
    {
        await using var harness = await StartHarnessAsync();
        await harness.Database.KeyDeleteAsync(ValkeyKeys.LegalParametersVersion);
        var version = new ValkeyLegalParameterCacheVersion(
            harness.Gateway, TimeProvider.System, NullLogger<ValkeyLegalParameterCacheVersion>.Instance);
        var before = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();

        await version.BumpAsync(Ct);

        var stored = (long)await harness.Database.StringGetAsync(ValkeyKeys.LegalParametersVersion);
        Assert.True(stored > before, $"{stored} <= {before}");
    }

    [Fact]
    public async Task TheTableExpiresInOneHour()
    {
        await using var harness = await StartHarnessAsync();
        var inner = new CountingSource();
        await NewSource(inner, harness).ListAsync(Ct);
        var current = (long)await harness.Database.StringGetAsync(ValkeyKeys.LegalParametersVersion);

        var ttl = await harness.Database.KeyTimeToLiveAsync(ValkeyKeys.LegalParametersTable(current));

        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));
    }

    // Review Focus 4: el lector que arrancó antes del commit no puede dejar su tabla vieja bajo la versión nueva.
    [Fact]
    public async Task AReadThatStartedBeforeTheCommitCannotOverwriteTheNewVersion()
    {
        await using var harness = await StartHarnessAsync();
        var inner = new CountingSource();
        var version = new ValkeyLegalParameterCacheVersion(
            harness.Gateway, TimeProvider.System, NullLogger<ValkeyLegalParameterCacheVersion>.Instance);
        await version.BumpAsync(Ct);
        inner.HoldNextLoad();
        var late = NewSource(inner, harness).ListAsync(Ct);
        await inner.Started;

        inner.Smmlv = 2_000_000m;
        await version.BumpAsync(Ct);
        inner.Release();
        var stale = await late;
        var fresh = await NewSource(inner, harness).ListAsync(Ct);

        Assert.Equal(1_750_905m, Assert.Single(stale).Value);
        Assert.Equal(2, inner.Loads);
        Assert.Equal(2_000_000m, Assert.Single(fresh).Value);
    }

    [Fact]
    public async Task ValkeyDownReadsTheDatabase()
    {
        await using var harness = await Harness.StartAsync("localhost:1,abortConnect=false", connected: false);
        var inner = new CountingSource();

        var values = await NewSource(inner, harness).ListAsync(Ct);

        Assert.Equal(1_750_905m, Assert.Single(values).Value);
        Assert.Equal(1, inner.Loads);
        Assert.Equal(1, harness.Fallbacks.Count("legal_parameters"));
    }

    private sealed class ErrorLogger : ILogger<ValkeyLegalParameterCacheVersion>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }
    }

    [Fact]
    public async Task BumpFailureIsLoggedAndDoesNotThrow()
    {
        await using var harness = await Harness.StartAsync("localhost:1,abortConnect=false", connected: false);
        var logger = new ErrorLogger();
        var version = new ValkeyLegalParameterCacheVersion(harness.Gateway, TimeProvider.System, logger);

        await version.BumpAsync(CancellationToken.None);

        Assert.Equal(1, harness.Fallbacks.Count("legal_parameters"));
        var error = Assert.Single(logger.Errors);
        Assert.Contains("1 hour", error, StringComparison.Ordinal);
    }
}
