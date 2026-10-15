using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Application;
using Npgsql;

namespace Modules.Messaging.Infrastructure.Realtime;

/// <summary>
/// Spec 2026-10-14, decisión 6: cada pod mantiene una conexión propia en <c>LISTEN messaging_events</c> y reparte lo que
/// llega a todos los <see cref="IMessagingEventSink"/> registrados (el del hub en producción; uno de prueba en la suite).
/// Postgres también le entrega al pod que publicó, así que el que procesa el webhook y el que tiene al asesor
/// conectado pueden ser el mismo o distintos sin que nadie lo coordine. Si la conexión se cae, reabre con backoff y
/// lo registra sin el payload; mientras tanto el polling relajado del frontend cubre el hueco. Nunca tumba el host
/// (mismo criterio que <c>ExportLoadSeedWorker</c>).
/// </summary>
internal sealed partial class MessagingEventsListener(
    IConfiguration configuration,
    IEnumerable<IMessagingEventSink> sinks,
    ILogger<MessagingEventsListener> logger) : BackgroundService
{
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
    ];

    private readonly IReadOnlyList<IMessagingEventSink> _sinks = [.. sinks];

    [LoggerMessage(Level = LogLevel.Information, Message = "Messaging events listener is listening on channel {Channel}.")]
    private static partial void LogListening(ILogger logger, string channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Messaging events listener lost its connection; it reconnects in {Delay}.")]
    private static partial void LogDisconnected(ILogger logger, TimeSpan delay, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Messaging events listener dropped a notification it could not read.")]
    private static partial void LogUnreadable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A messaging event sink failed for {Kind} of conversation {ConversationId}; the other sinks are not affected.")]
    private static partial void LogSinkFailed(ILogger logger, string kind, Guid conversationId, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Yield();
                await ListenAsync(connectionString, stoppingToken);
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

    private async Task ListenAsync(string connectionString, CancellationToken stoppingToken)
    {
        // Conexión propia y sin pool: una conexión en LISTEN no puede volver al pool, y Npgsql exige que nadie más
        // la use mientras WaitAsync espera.
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, KeepAlive = 30 };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(stoppingToken);
        connection.Notification += (_, args) => OnNotification(args.Payload);

        await using (var listen = new NpgsqlCommand($"LISTEN {MessagingEventJson.Channel}", connection))
        {
            await listen.ExecuteNonQueryAsync(stoppingToken);
        }

        LogListening(logger, MessagingEventJson.Channel);
        while (!stoppingToken.IsCancellationRequested)
        {
            await connection.WaitAsync(stoppingToken);
        }
    }

    private void OnNotification(string payload)
    {
        var @event = MessagingEventJson.TryDeserialize(payload);
        if (@event is null)
        {
            LogUnreadable(logger);
            return;
        }

        // Fuera del hilo de la conexión: el handler de Notification corre dentro de WaitAsync, y un sink lento
        // (SignalR, red) no debe frenar la lectura de las siguientes notificaciones.
        _ = Task.Run(() => DispatchAsync(@event));
    }

    private async Task DispatchAsync(MessagingEvent @event)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.DispatchAsync(@event, CancellationToken.None);
            }
            catch (Exception exception)
            {
                LogSinkFailed(logger, @event.Kind, @event.ConversationId, exception);
            }
        }
    }
}
