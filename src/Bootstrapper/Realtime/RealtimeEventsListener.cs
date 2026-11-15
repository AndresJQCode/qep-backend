using System.Threading.Channels;
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
///
/// Notices are delivered in the order Postgres sent them: the notification callback only queues the envelope, and a single consumer
/// dispatches one at a time. <c>presenceChanged</c> carries the full state, so two of them delivered out of order (composing, then
/// none, arriving reversed) would leave a phantom until the next change.
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

    // Unbounded on purpose: the callback runs inside WaitAsync and must never block or drop; notices are small and the consumer only
    // hands them to SignalR.
    private readonly Channel<RealtimeEnvelope> _pending = Channel.CreateUnbounded<RealtimeEnvelope>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

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
        var consumer = Task.Run(() => ConsumeAsync(stoppingToken), CancellationToken.None);
        try
        {
            await ListenWithBackoffAsync(stoppingToken);
        }
        finally
        {
            _pending.Writer.TryComplete();
            await consumer;
        }
    }

    private async Task ListenWithBackoffAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Yield();
                // Inside the guarded loop: a missing connection string is logged and retried with backoff like any other failure,
                // instead of escaping ExecuteAsync and stopping the host.
                var (connectionString, keyName) = StartupLockConnectionString.Resolve(configuration);
                // ListenAsync only returns on cancellation, so the backoff resets as soon as LISTEN succeeds: a connection that
                // dropped after hours listening starts again from the shortest delay, not from where the last outage left it.
                await ListenAsync(connectionString, keyName, () => attempt = 0, stoppingToken);
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

    private async Task ListenAsync(string connectionString, string keyName, Action onListening, CancellationToken stoppingToken)
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

        onListening();
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
        // Queued rather than Task.Run: the single consumer keeps the order Postgres delivered.
        _pending.Writer.TryWrite(envelope);
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var envelope in _pending.Reader.ReadAllAsync(stoppingToken))
            {
                await DispatchAsync(envelope);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: whatever is still queued is for connections that are closing too.
        }
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
