using System.Diagnostics;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.ApiAccess.Application;

namespace Bootstrapper.ApiKeys;

/// <summary>
/// Deja una fila por cada llamada identificada a la API externa (spec 2026-11-01 parte 1). Escribe en <c>OnCompleted</c> y en un
/// scope propio: con la respuesta ya entregada, la base no le suma tiempo al envío ni comparte transacción con él. Va afuera de
/// autenticación, porque un 401 o un 403 también son una llamada, y adentro del manejador de excepciones, que por eso anota su código
/// en la nota antes de que corra el callback.
/// </summary>
public sealed partial class ExternalApiCallLoggingMiddleware(
    RequestDelegate next,
    IServiceScopeFactory scopes,
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

        context.Response.OnCompleted(async () =>
        {
            try
            {
                var durationMs = (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                if (note.ToRequest(method, path, context.Response.StatusCode, traceId, durationMs, occurredAt) is not { } row)
                {
                    return;
                }

                await using var scope = scopes.CreateAsyncScope();

                // CancellationToken.None: el del request ya no vale nada acá, y una llamada cortada por el cliente también se registra.
                await scope.ServiceProvider.GetRequiredService<IApiKeyRequestLog>().RecordAsync(row, CancellationToken.None);
            }
#pragma warning disable CA1031 // El registro nunca rompe una respuesta ya entregada.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogRecordingFailed(logger, exception, method, path);
            }
        });

        return next(context);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Could not record the external API call {Method} {Path}.")]
    private static partial void LogRecordingFailed(ILogger logger, Exception exception, string method, string path);
}
