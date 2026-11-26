using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Bootstrapper.Valkey;

/// <summary>El resultado de una operación contra Valkey: sin éxito, quien llama usa su respaldo local.</summary>
internal readonly record struct ValkeyAttempt<T>(bool Succeeded, T? Value);

/// <summary>
/// La única puerta de los adaptadores hacia Valkey (spec 2026-11-13, D-1). Sin conexión vuelve al instante, sin esperar el
/// tope (V4 del plan: con Valkey caído cada request pagaría <c>OperationTimeoutMs</c>); con conexión, la operación tiene
/// <c>OperationTimeoutMs</c> para responder. Todo lo que no sea la cancelación de quien llama cuenta un respaldo del área y
/// se loguea en <c>Debug</c> con el área y el tipo de excepción (V5): el mensaje de StackExchange.Redis lleva
/// <c>host:puerto</c>, y un <c>Warning</c> por request inundaría el log en una caída. El <c>Warning</c> sale de
/// <see cref="ValkeyConnection"/>.
/// </summary>
internal sealed partial class ValkeyGateway(
    ValkeyConnection connection,
    IOptions<ValkeyOptions> options,
    ValkeyMetrics metrics,
    ILogger<ValkeyGateway> logger)
{
    private const string NotConnected = "NotConnected";

    public bool IsEnabled(ValkeyArea area) => options.Value.Enabled && ValkeyAreas.IsOn(options.Value.Areas, area);

    public async Task<ValkeyAttempt<T>> TryAsync<T>(
        ValkeyArea area,
        Func<IDatabase, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (connection.Multiplexer is not { IsConnected: true } multiplexer)
        {
            return Fallback<T>(area, NotConnected);
        }

        Task<T>? pending = null;
        var started = Stopwatch.GetTimestamp();
        try
        {
            pending = operation(multiplexer.GetDatabase());
            var value = await pending
                .WaitAsync(TimeSpan.FromMilliseconds(options.Value.OperationTimeoutMs), cancellationToken)
                .ConfigureAwait(false);
            metrics.RecordOperation(area, ValkeyOperationOutcome.Ok, Stopwatch.GetElapsedTime(started));
            return new ValkeyAttempt<T>(true, value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Observe(pending);
            throw;
        }
        catch (Exception exception)
        {
            MeasureFailed(area, pending, started);
            return Fallback<T>(area, exception.GetType().Name);
        }
    }

    // Una operación que sigue viva se soltó por tope: se mide cuando termine, en el mismo hilo que la completa para no sumar
    // otra espera. La continuación además observa su excepción. Si ya terminó, la duración es la de ahora.
    private void MeasureFailed(ValkeyArea area, Task? pending, long started)
    {
        if (pending is { IsCompleted: false })
        {
            _ = pending.ContinueWith(
                task =>
                {
                    _ = task.Exception;
                    metrics.RecordOperation(
                        area,
                        task.IsCompletedSuccessfully ? ValkeyOperationOutcome.Late : ValkeyOperationOutcome.LateError,
                        Stopwatch.GetElapsedTime(started));
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return;
        }

        // Terminó justo entre el tope y este punto: respondió, pero tarde.
        var outcome = pending is { IsCompletedSuccessfully: true }
            ? ValkeyOperationOutcome.Late
            : ValkeyOperationOutcome.Error;
        metrics.RecordOperation(area, outcome, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>El multiplexer existe y está conectado ahora mismo.</summary>
    public bool IsConnected => connection.Multiplexer is { IsConnected: true };

    /// <summary>
    /// <c>PUBLISH</c> sin esperar la respuesta (<see cref="CommandFlags.FireAndForget"/>), para que un timbre nunca alargue el
    /// commit de quien lo toca (D-7, plan P8). Sin conexión o con cualquier excepción cuenta el respaldo del área. Nunca lanza.
    /// </summary>
    public void Publish(ValkeyArea area, RedisChannel channel)
    {
        if (connection.Multiplexer is not { IsConnected: true } multiplexer)
        {
            RecordFallback(area, NotConnected);
            return;
        }

        try
        {
            multiplexer.GetSubscriber().Publish(channel, RedisValue.EmptyString, CommandFlags.FireAndForget);
        }
        catch (Exception exception)
        {
            RecordFallback(area, exception.GetType().Name);
        }
    }

    /// <summary>
    /// <c>SUBSCRIBE</c> con el tope de <c>OperationTimeoutMs</c>. <c>false</c> sin conexión o con error (cuenta el respaldo),
    /// y en ese caso suelta el handler, para que un reintento no deje dos. StackExchange.Redis vuelve a suscribir solo al
    /// reconectar.
    /// </summary>
    public async Task<bool> TrySubscribeAsync(
        ValkeyArea area,
        RedisChannel channel,
        Action handler,
        CancellationToken cancellationToken)
    {
        if (connection.Multiplexer is not { IsConnected: true } multiplexer)
        {
            RecordFallback(area, NotConnected);
            return false;
        }

        // GetSubscriber dentro del try: con un multiplexer que se está disponiendo lanza, y esa excepción no puede subir hasta el
        // BackgroundService del worker (lo detendría y, con el comportamiento por defecto, tumbaría el host).
        ISubscriber? subscriber = null;
        Action<RedisChannel, RedisValue> onMessage = (_, _) => handler();
        Task? pending = null;
        try
        {
            subscriber = multiplexer.GetSubscriber();
            pending = subscriber.SubscribeAsync(channel, onMessage);
            await pending
                .WaitAsync(TimeSpan.FromMilliseconds(options.Value.OperationTimeoutMs), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Observe(pending);
            Unsubscribe(subscriber, channel, onMessage);
            throw;
        }
        catch (Exception exception)
        {
            Observe(pending);
            Unsubscribe(subscriber, channel, onMessage);
            RecordFallback(area, exception.GetType().Name);
            return false;
        }
    }

    private static void Unsubscribe(ISubscriber? subscriber, RedisChannel channel, Action<RedisChannel, RedisValue> handler)
    {
        try
        {
            subscriber?.Unsubscribe(channel, handler, CommandFlags.FireAndForget);
        }
        catch (Exception)
        {
            // Sin conexión no hay nada que soltar en el servidor; el handler local ya no recibe nada.
        }
    }

    private ValkeyAttempt<T> Fallback<T>(ValkeyArea area, string reason)
    {
        RecordFallback(area, reason);
        return new ValkeyAttempt<T>(false, default);
    }

    private void RecordFallback(ValkeyArea area, string reason)
    {
        metrics.RecordFallback(area, reason);
        LogFallback(logger, area, reason);
    }

    // La operación que se soltó por tope sigue viva: si después falla, su excepción no tiene que quedar sin observar.
    private static void Observe(Task? pending)
    {
        if (pending is { IsCompleted: false })
        {
            _ = pending.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Valkey fallback in area {Area}: {Reason}.")]
    private static partial void LogFallback(ILogger logger, ValkeyArea area, string reason);
}
