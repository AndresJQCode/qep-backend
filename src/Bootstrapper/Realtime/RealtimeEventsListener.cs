using Bootstrapper.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bootstrapper.Realtime;

/// <summary>
/// Plan N7: each pod keeps its own connection in <c>LISTEN qep_realtime</c> and hands what arrives to <see cref="AppHubDispatcher"/>
/// (copy of <c>MessagingEventsListener</c>). Postgres also delivers to the pod that published. The connection is direct and unpooled,
/// resolved like the startup lock (<see cref="StartupLockConnectionString"/>): <c>LISTEN</c> does not survive PgBouncer in
/// <c>transaction</c> mode and fails silently. It does not share Messaging's listener connection, whose internals Bootstrapper must
/// not open; two <c>LISTEN</c> per pod until Messaging moves to this hub. It reconnects with backoff and never brings the host down.
/// </summary>
internal sealed partial class RealtimeEventsListener(
    IConfiguration configuration,
    AppHubDispatcher dispatcher,
    ILogger<RealtimeEventsListener> logger) : BackgroundService
{
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
    ];

    [LoggerMessage(Level = LogLevel.Information, Message = "Realtime events listener is listening on channel {Channel} using connection string key {ConnectionStringKey}.")]
    private static partial void LogListening(ILogger logger, string channel, string connectionStringKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime events listener lost its connection; it reconnects in {Delay}.")]
    private static partial void LogDisconnected(ILogger logger, TimeSpan delay, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime events listener dropped a notification it could not read.")]
    private static partial void LogUnreadable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivering realtime notice {Kind} of tenant {TenantId} failed.")]
    private static partial void LogDispatchFailed(ILogger logger, string kind, Guid tenantId, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var (connectionString, keyName) = StartupLockConnectionString.Resolve(configuration);
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Yield();
                await ListenAsync(connectionString, keyName, stoppingToken);
                attempt = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                var delay = ReconnectDelays[Math.Min(attempt, ReconnectDelays.Length - 1)];
                attempt++;
                LogDisconnected(logger, delay, exception);
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ListenAsync(string connectionString, string keyName, CancellationToken stoppingToken)
    {
        // Own connection, no pool: a connection in LISTEN cannot go back to the pool, and Npgsql requires nobody else to use it
        // while WaitAsync waits.
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, KeepAlive = 30 };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(stoppingToken);
        connection.Notification += (_, args) => OnNotification(args.Payload);

        await using (var listen = new NpgsqlCommand($"LISTEN {RealtimeEventJson.Channel}", connection))
        {
            await listen.ExecuteNonQueryAsync(stoppingToken);
        }

        LogListening(logger, RealtimeEventJson.Channel, keyName);
        while (!stoppingToken.IsCancellationRequested)
        {
            await connection.WaitAsync(stoppingToken);
        }
    }

    private void OnNotification(string payload)
    {
        var envelope = RealtimeEventJson.TryDeserialize(payload);
        if (envelope is null)
        {
            LogUnreadable(logger);
            return;
        }

        // Off the connection's thread: Notification runs inside WaitAsync, and a slow SignalR send must not hold the next notices.
        _ = Task.Run(() => DispatchAsync(envelope));
    }

    private async Task DispatchAsync(RealtimeEnvelope envelope)
    {
        try
        {
            await dispatcher.DispatchAsync(envelope, CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogDispatchFailed(logger, envelope.Kind, envelope.TenantId, exception);
        }
    }
}
