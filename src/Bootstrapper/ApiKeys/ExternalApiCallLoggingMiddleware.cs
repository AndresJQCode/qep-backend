using System.Diagnostics;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Bootstrapper.ApiKeys;

/// <summary>
/// Deja una fila por cada llamada identificada a la API externa (spec 2026-11-01 parte 1). En <c>OnCompleted</c> sólo arma la fila y
/// la encola en <see cref="ExternalApiCallLogChannel"/>: el INSERT lo hace <see cref="ExternalApiCallLogWriter"/>, porque Kestrel espera
/// ese callback antes de leer la siguiente petición de la misma conexión y una escritura ahí demoraría la próxima llamada del
/// integrador. Va afuera de autenticación, porque un 401 o un 403 también son una llamada, y adentro del manejador de excepciones,
/// que por eso anota su código en la nota antes de que corra el callback.
/// </summary>
public sealed partial class ExternalApiCallLoggingMiddleware(
    RequestDelegate next,
    ExternalApiCallLogChannel queue,
    ILogger<ExternalApiCallLoggingMiddleware> logger)
{
    public Task InvokeAsync(HttpContext context, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(clock);

        if (!ExternalApiPaths.IsExternal(context.Request.Path))
        {
            return next(context);
        }

        var note = ExternalCallNote.Attach(context);
        var method = context.Request.Method;

        // Nunca QueryString: el registro no guarda nada de lo que manda el integrador.
        var path = context.Request.Path.Value ?? string.Empty;
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        var occurredAt = clock.UtcNow;
        var startedAt = Stopwatch.GetTimestamp();

        context.Response.OnCompleted(() =>
        {
            try
            {
                var durationMs = (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                if (note.ToRequest(method, path, context.Response.StatusCode, traceId, durationMs, occurredAt) is { } row
                    && !queue.TryEnqueue(row))
                {
                    // Cola llena: se descarta la fila. Nunca se espera, porque esperar es demorar la respuesta que el registro no puede demorar.
                    LogQueueFull(logger, method, path);
                }
            }
#pragma warning disable CA1031 // El registro nunca rompe una respuesta ya entregada.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogRecordingFailed(logger, exception, method, path);
            }

            return Task.CompletedTask;
        });

        return next(context);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Could not record the external API call {Method} {Path}.")]
    private static partial void LogRecordingFailed(ILogger logger, Exception exception, string method, string path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "The external API call log queue is full; dropped {Method} {Path}.")]
    private static partial void LogQueueFull(ILogger logger, string method, string path);
}
