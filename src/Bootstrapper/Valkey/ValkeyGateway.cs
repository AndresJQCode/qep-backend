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
        try
        {
            pending = operation(multiplexer.GetDatabase());
            var value = await pending
                .WaitAsync(TimeSpan.FromMilliseconds(options.Value.OperationTimeoutMs), cancellationToken)
                .ConfigureAwait(false);
            return new ValkeyAttempt<T>(true, value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Observe(pending);
            throw;
        }
        catch (Exception exception)
        {
            Observe(pending);
            return Fallback<T>(area, exception.GetType().Name);
        }
    }

    private ValkeyAttempt<T> Fallback<T>(ValkeyArea area, string reason)
    {
        metrics.RecordFallback(area);
        LogFallback(logger, area, reason);
        return new ValkeyAttempt<T>(false, default);
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
