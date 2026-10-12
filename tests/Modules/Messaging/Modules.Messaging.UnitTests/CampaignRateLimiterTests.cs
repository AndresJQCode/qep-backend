using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Un reloj que sólo avanza cuando alguien lo espera: <c>Delay</c> de la prueba lo mueve en lugar de dormir.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public TimeSpan Elapsed => TimeSpan.FromTicks(_ticks);

    public void Advance(TimeSpan span) => _ticks += span.Ticks;

    /// <summary>El «Task.Delay» de las pruebas: no duerme, avanza el reloj y anota cuánto se pidió esperar.</summary>
    public Task Delay(TimeSpan span, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Advance(span);
        return Task.CompletedTask;
    }
}

/// <summary>Spec 2026-10-11 decisión 14 y §6.3 paso 3.4: un token bucket en memoria por conexión. Con 20 por segundo, el primer segundo
/// sale de golpe y de ahí en adelante uno cada 50 ms; otra conexión tiene su propio cubo.</summary>
public sealed class CampaignRateLimiterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (CampaignRateLimiter Limiter, ManualTimeProvider Time) Build(int perSecond)
    {
        var time = new ManualTimeProvider();
        return (new CampaignRateLimiter(perSecond, time, time.Delay), time);
    }

    [Fact]
    public async Task TheFirstSecondGoesAtOnceAndTheRestAtTheConfiguredRate()
    {
        var (limiter, time) = Build(20);
        var connection = Guid.CreateVersion7();

        for (var index = 0; index < 20; index++)
        {
            await limiter.WaitAsync(connection, Ct);
        }

        Assert.Equal(TimeSpan.Zero, time.Elapsed);

        for (var index = 0; index < 40; index++)
        {
            await limiter.WaitAsync(connection, Ct);
        }

        Assert.InRange(time.Elapsed.TotalSeconds, 1.99, 2.01);
    }

    [Fact]
    public async Task EachConnectionHasItsOwnBucket()
    {
        var (limiter, time) = Build(20);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        for (var index = 0; index < 20; index++)
        {
            await limiter.WaitAsync(first, Ct);
            await limiter.WaitAsync(second, Ct);
        }

        Assert.Equal(TimeSpan.Zero, time.Elapsed);
    }

    [Fact]
    public async Task ASlowRateSpacesTheCallsOutAndIdleTimeRefillsOnlyOneBurst()
    {
        var (limiter, time) = Build(1);
        var connection = Guid.CreateVersion7();

        await limiter.WaitAsync(connection, Ct);
        await limiter.WaitAsync(connection, Ct);
        await limiter.WaitAsync(connection, Ct);
        Assert.InRange(time.Elapsed.TotalSeconds, 1.99, 2.01);

        // Una hora sin enviar no deja 3600 tokens guardados: el cubo se llena hasta su capacidad (1) y nada más.
        time.Advance(TimeSpan.FromHours(1));
        var before = time.Elapsed;
        await limiter.WaitAsync(connection, Ct);
        await limiter.WaitAsync(connection, Ct);
        Assert.InRange((time.Elapsed - before).TotalSeconds, 0.99, 1.01);
    }

    [Fact]
    public async Task ACancelledWaitThrows()
    {
        var (limiter, _) = Build(1);
        var connection = Guid.CreateVersion7();
        await limiter.WaitAsync(connection, Ct);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.WaitAsync(connection, cancelled.Token));
    }

    [Fact]
    public void ARateBelowOneIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CampaignRateLimiter(0, TimeProvider.System, Task.Delay));
}
