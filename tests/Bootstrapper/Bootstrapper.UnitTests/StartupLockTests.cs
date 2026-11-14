using Bootstrapper.Startup;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-29 (réplicas múltiples), D-2: el candado de arranque es un semáforo entre pods. Contra PostgreSQL real
/// porque lo que se prueba es la espera del advisory lock, el <c>CommandTimeout</c> de Npgsql y el
/// <c>idle_in_transaction_session_timeout</c> del servidor. Un contenedor para toda la clase: cada prueba suelta lo que
/// toma, y la del timeout ocioso lo deja puesto en la base, cosa que no afecta a las demás (siempre lo esquivan).
/// </summary>
public sealed class StartupLockTests(StartupLockTests.Database database) : IClassFixture<StartupLockTests.Database>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ConnectionString => database.ConnectionString;

    [Fact]
    public async Task TheStartupWorkWaitsWhileAnotherInstanceHoldsTheLock()
    {
        var held = await HoldLockAsync(ConnectionString);
        var started = false;

        var run = StartupLock.RunAsync(
            ConnectionString, "QepDatabase", NullLogger.Instance,
            _ => { started = true; return Task.CompletedTask; }, Ct);

        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.False(started);
        Assert.False(run.IsCompleted);

        await held.DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(started);
    }

    [Fact]
    public async Task TheLockIsReleasedWhenTheStartupWorkThrows()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => StartupLock.RunAsync(
            ConnectionString, "QepDatabase", NullLogger.Instance,
            _ => throw new InvalidOperationException("boom"), Ct));

        Assert.True(await IsFreeAsync(ConnectionString));
    }

    [Fact]
    public async Task CancellingWhileWaitingStopsWithoutTakingTheLock()
    {
        var held = await HoldLockAsync(ConnectionString);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
        var started = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartupLock.RunAsync(
            ConnectionString, "QepDatabase", NullLogger.Instance,
            _ => { started = true; return Task.CompletedTask; }, cancellation.Token));

        Assert.False(started);
        await held.DisposeAsync();
        Assert.True(await IsFreeAsync(ConnectionString));
    }

    [Fact]
    public async Task TheWaitOutlastsTheCommandTimeout()
    {
        var shortTimeout = new NpgsqlConnectionStringBuilder(ConnectionString) { CommandTimeout = 1 }.ConnectionString;
        var held = await HoldLockAsync(ConnectionString);
        var started = false;

        var run = StartupLock.RunAsync(
            shortTimeout, "QepDatabase", NullLogger.Instance,
            _ => { started = true; return Task.CompletedTask; }, Ct);

        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        await held.DisposeAsync();

        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(started);
    }

    [Fact]
    public async Task TheLockStaysHeldWhileTheWorkRunsDespiteAnIdleTransactionTimeout()
    {
        // Aplica a las conexiones nuevas; RunAsync abre la suya sin pool, así que la recibe.
        await ExecuteAsync(ConnectionString, "ALTER DATABASE qep SET idle_in_transaction_session_timeout = '1s'");
        try
        {
            bool? freeWhileWorking = null;

            await StartupLock.RunAsync(
                ConnectionString, "QepDatabase", NullLogger.Instance,
                async token =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), token);
                    freeWhileWorking = await IsFreeAsync(ConnectionString);
                }, Ct);

            Assert.False(freeWhileWorking);
            Assert.True(await IsFreeAsync(ConnectionString));
        }
        finally
        {
            await ExecuteAsync(ConnectionString, "ALTER DATABASE qep RESET idle_in_transaction_session_timeout");
        }
    }

    /// <summary>Toma el candado como lo haría otro pod: su propia conexión y transacción. Al soltarlo, <c>COMMIT</c>.</summary>
    private static async Task<IAsyncDisposable> HoldLockAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@namespace, @key)", connection, transaction))
        {
            command.Parameters.AddWithValue("namespace", StartupLock.Namespace);
            command.Parameters.AddWithValue("key", StartupLock.Key);
            await command.ExecuteNonQueryAsync(Ct);
        }

        return new HeldLock(connection, transaction);
    }

    private static async Task<bool> IsFreeAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(20261113, 1)", connection, transaction);
        var free = (bool)(await command.ExecuteScalarAsync(Ct))!;
        await transaction.RollbackAsync(Ct);
        return free;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private sealed class HeldLock(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        private bool disposed;

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            await transaction.CommitAsync(CancellationToken.None);
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    public sealed class Database : IAsyncLifetime
    {
        private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();

        public string ConnectionString => container.GetConnectionString();

        public async ValueTask InitializeAsync() => await container.StartAsync();

        public async ValueTask DisposeAsync() => await container.DisposeAsync();
    }
}
