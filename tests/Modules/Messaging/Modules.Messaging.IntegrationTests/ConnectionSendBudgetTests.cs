using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Decisión del owner 2026-10-12 (P3 y P5): el límite de envío por conexión es un token bucket en Postgres, compartido por todas las
/// réplicas, con reloj de mentira (el <c>now</c> entra por parámetro). Cada «pod» de estas pruebas es un scope propio, con su
/// <c>DbContext</c>: lo único que comparten es la base. Por defecto: 20 mps de objetivo y 80 de techo.</summary>
public sealed class ConnectionSendBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, Guid TenantId, Guid ConnectionId);

    private static Fixture Arrange(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        using var started = factory.CreateClient();
        return new Fixture(factory, connectionString, Guid.CreateVersion7(), Guid.CreateVersion7());
    }

    /// <summary>Un «pod»: su propio scope, su propio DbContext.</summary>
    private static (IServiceScope Scope, IConnectionSendBudget Budget) Pod(Fixture f)
    {
        var scope = f.Factory.Services.CreateScope();
        return (scope, scope.ServiceProvider.GetRequiredService<IConnectionSendBudget>());
    }

    [Fact]
    public async Task TheFirstUseGrantsTheTargetAsABurstAndThenOnlyWhatHasAccrued()
    {
        await using var database = await StartDatabaseAsync();
        var f = Arrange(database);
        using var host = f.Factory;
        var (scope, budget) = Pod(f);
        using var podScope = scope;

        var burst = await budget.AcquireAsync(f.TenantId, f.ConnectionId, 100, T0, Ct);
        var half = await budget.AcquireAsync(f.TenantId, f.ConnectionId, 100, T0.AddMilliseconds(500), Ct);
        var tooSoon = await budget.AcquireAsync(f.TenantId, f.ConnectionId, 100, T0.AddMilliseconds(500), Ct);

        Assert.Equal((20, 20d), (burst.Granted, burst.Rate));
        Assert.Equal(10, half.Granted);
        Assert.Equal(0, tooSoon.Granted);
        Assert.InRange(tooSoon.RetryAfter.TotalMilliseconds, 49, 51);
    }

    [Fact]
    public async Task TheBucketNeverHoldsMoreThanOneSecondOfTokens()
    {
        await using var database = await StartDatabaseAsync();
        var f = Arrange(database);
        using var host = f.Factory;
        var (scope, budget) = Pod(f);
        using var podScope = scope;

        _ = await budget.AcquireAsync(f.TenantId, f.ConnectionId, 100, T0, Ct);
        var afterAMinute = await budget.AcquireAsync(f.TenantId, f.ConnectionId, 100, T0.AddMinutes(1), Ct);

        Assert.Equal(20, afterAMinute.Granted);
    }

    [Fact]
    public async Task TwoPodsSharingTheConnectionNeverExceedTheRate()
    {
        await using var database = await StartDatabaseAsync();
        var f = Arrange(database);
        using var host = f.Factory;
        var (scopeA, podA) = Pod(f);
        var (scopeB, podB) = Pod(f);
        using var podScopeA = scopeA;
        using var podScopeB = scopeB;

        var granted = new List<(DateTimeOffset At, int Count, string Pod)>();
        for (var step = 0; step <= 200; step++)
        {
            var now = T0.AddMilliseconds(step * 50);
            // Quien pide primero se lleva el token del paso: se alternan para que los dos pods participen.
            var pods = step % 2 == 0 ? new[] { ("A", podA), ("B", podB) } : new[] { ("B", podB), ("A", podA) };
            foreach (var (name, pod) in pods)
            {
                granted.Add((now, (await pod.AcquireAsync(f.TenantId, f.ConnectionId, 100, now, Ct)).Granted, name));
            }
        }

        // Diez segundos: la ráfaga inicial (20) más 20 por segundo, sin importar cuántos pods pidan.
        var total = granted.Sum(entry => entry.Count);
        Assert.InRange(total, 20 + (20 * 10) - 1, 20 + (20 * 10));
        // Ninguna ventana de un segundo pasa de la ráfaga más lo que se acumula en ella.
        for (var second = 0; second < 10; second++)
        {
            var window = granted.Where(entry => entry.At >= T0.AddSeconds(second) && entry.At < T0.AddSeconds(second + 1)).Sum(entry => entry.Count);
            Assert.True(window <= 40, $"window {second}: {window}");
        }

        Assert.Contains(granted, entry => entry is { Pod: "A", Count: > 0 });
        Assert.Contains(granted, entry => entry is { Pod: "B", Count: > 0 });
    }

    [Fact]
    public async Task A130429ReportedByOnePodHalvesTheRateForBothAndPausesTheGrantsFor30Seconds()
    {
        await using var database = await StartDatabaseAsync();
        var f = Arrange(database);
        using var host = f.Factory;
        var (scopeA, podA) = Pod(f);
        var (scopeB, podB) = Pod(f);
        using var podScopeA = scopeA;
        using var podScopeB = scopeB;
        _ = await podA.AcquireAsync(f.TenantId, f.ConnectionId, 20, T0, Ct);

        await podA.ThrottleAsync(f.TenantId, f.ConnectionId, T0.AddSeconds(1), Ct);

        var duringA = await podA.AcquireAsync(f.TenantId, f.ConnectionId, 10, T0.AddSeconds(2), Ct);
        var duringB = await podB.AcquireAsync(f.TenantId, f.ConnectionId, 10, T0.AddSeconds(2), Ct);
        Assert.Equal((0, 0), (duringA.Granted, duringB.Granted));
        Assert.InRange(duringB.RetryAfter.TotalSeconds, 28.9, 29.1);
        Assert.Equal(10d, duringB.Rate);

        // Pasados los 30 s vuelve a conceder, pero a la mitad del ritmo, en los dos pods.
        var afterB = await podB.AcquireAsync(f.TenantId, f.ConnectionId, 100, T0.AddSeconds(32), Ct);
        Assert.Equal(10d, afterB.Rate);
        Assert.InRange(afterB.Granted, 9, 10);
        var afterA = await podA.AcquireAsync(f.TenantId, f.ConnectionId, 100, T0.AddSeconds(32.5), Ct);
        Assert.InRange(afterA.Granted, 4, 5);
    }

    [Fact]
    public async Task SeveralAnswersOf130429InTheSameBurstHalveTheRateOnlyOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = Arrange(database);
        using var host = f.Factory;
        var (scopeA, podA) = Pod(f);
        var (scopeB, podB) = Pod(f);
        using var podScopeA = scopeA;
        using var podScopeB = scopeB;
        _ = await podA.AcquireAsync(f.TenantId, f.ConnectionId, 1, T0, Ct);

        await podA.ThrottleAsync(f.TenantId, f.ConnectionId, T0.AddSeconds(1), Ct);
        await podB.ThrottleAsync(f.TenantId, f.ConnectionId, T0.AddSeconds(1.2), Ct);
        await podA.ThrottleAsync(f.TenantId, f.ConnectionId, T0.AddSeconds(2), Ct);

        Assert.Equal(10d, (await podA.AcquireAsync(f.TenantId, f.ConnectionId, 1, T0.AddSeconds(3), Ct)).Rate);
    }

    [Fact]
    public async Task TheRateNeverFallsBelowOne()
    {
        await using var database = await StartDatabaseAsync();
        var f = Arrange(database);
        using var host = f.Factory;
        var (scope, budget) = Pod(f);
        using var podScope = scope;
        _ = await budget.AcquireAsync(f.TenantId, f.ConnectionId, 1, T0, Ct);

        // 20 → 10 → 5 → 2.5 → 1.25 → 1 → 1: cada 130429 va a pasados los 30 s del anterior.
        for (var index = 1; index <= 8; index++)
        {
            await budget.ThrottleAsync(f.TenantId, f.ConnectionId, T0.AddSeconds(31 * index), Ct);
        }

        Assert.Equal(1d, (await budget.AcquireAsync(f.TenantId, f.ConnectionId, 1, T0.AddSeconds(31 * 8) + TimeSpan.FromSeconds(1), Ct)).Rate);
    }

    [Fact]
    public async Task WithoutNewAnswersOf130429TheRateClimbsOnePerThirtySecondsUpToTheTarget()
    {
        await using var database = await StartDatabaseAsync();
        var f = Arrange(database);
        using var host = f.Factory;
        var (scope, budget) = Pod(f);
        using var podScope = scope;
        _ = await budget.AcquireAsync(f.TenantId, f.ConnectionId, 1, T0, Ct);
        await budget.ThrottleAsync(f.TenantId, f.ConnectionId, T0, Ct);

        // La pausa termina a los 30 s; desde ahí se suma 1 mps cada 30 s.
        Assert.Equal(10d, (await budget.AcquireAsync(f.TenantId, f.ConnectionId, 1, T0.AddSeconds(31), Ct)).Rate);
        Assert.Equal(11d, (await budget.AcquireAsync(f.TenantId, f.ConnectionId, 1, T0.AddSeconds(61), Ct)).Rate);
        Assert.Equal(11d, (await budget.AcquireAsync(f.TenantId, f.ConnectionId, 1, T0.AddSeconds(80), Ct)).Rate);
        Assert.Equal(13d, (await budget.AcquireAsync(f.TenantId, f.ConnectionId, 1, T0.AddSeconds(125), Ct)).Rate);
        Assert.Equal(20d, (await budget.AcquireAsync(f.TenantId, f.ConnectionId, 1, T0.AddMinutes(30), Ct)).Rate);
        Assert.Equal(20d, (await budget.AcquireAsync(f.TenantId, f.ConnectionId, 1, T0.AddMinutes(60), Ct)).Rate);
    }

    [Fact]
    public async Task EachConnectionHasItsOwnBucket()
    {
        await using var database = await StartDatabaseAsync();
        var f = Arrange(database);
        using var host = f.Factory;
        var (scope, budget) = Pod(f);
        using var podScope = scope;
        var other = Guid.CreateVersion7();

        _ = await budget.AcquireAsync(f.TenantId, f.ConnectionId, 100, T0, Ct);
        await budget.ThrottleAsync(f.TenantId, f.ConnectionId, T0, Ct);

        var otherGrant = await budget.AcquireAsync(f.TenantId, other, 100, T0, Ct);
        Assert.Equal((20, 20d), (otherGrant.Granted, otherGrant.Rate));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.connection_send_budgets"));
    }

    [Fact]
    public async Task AThrottleOnAConnectionThatNeverSentStartsFromTheHalvedRate()
    {
        await using var database = await StartDatabaseAsync();
        var f = Arrange(database);
        using var host = f.Factory;
        var (scope, budget) = Pod(f);
        using var podScope = scope;

        await budget.ThrottleAsync(f.TenantId, f.ConnectionId, T0, Ct);

        var grant = await budget.AcquireAsync(f.TenantId, f.ConnectionId, 100, T0.AddSeconds(31), Ct);
        Assert.Equal(10d, grant.Rate);
        Assert.InRange(grant.Granted, 9, 10);
    }
}
