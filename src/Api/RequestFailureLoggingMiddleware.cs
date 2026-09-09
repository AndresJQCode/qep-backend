using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Platform.Application;

namespace Api;

/// <summary>
/// Registra las fallas que <see cref="ApiExceptionHandler"/> **no puede ver**.
///
/// El manejador de excepciones sólo corre cuando algo tira. Y buena parte de los requests que
/// fallan no tiran nada: el 401 lo escribe el middleware de autenticación, el 403 la política de
/// autorización, el 404 el enrutador y el 429 el limitador. Todos terminan con un código de
/// estado de error y sin una sola excepción, así que sin esto el log mostraría los errores de
/// negocio y se perdería justo los de acceso — que son los que alguien busca cuando dice "no me
/// deja guardar".
///
/// Va **afuera** de autenticación y autorización en la tubería, que es la única posición desde la
/// que se puede leer el código de estado que ellas escriben.
///
/// No duplica: el manejador marca en <see cref="HttpContext.Items"/> lo que ya registró, y acá se
/// saltea. Sin esa marca, cada 422 de negocio quedaría dos veces.
/// </summary>
internal sealed class RequestFailureLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestFailureLoggingMiddleware> logger)
{
    /// <summary>La marca que deja <see cref="ApiExceptionHandler"/> cuando ya registró la falla.</summary>
    public const string HandledKey = "qep.request_failure_logged";

    private static readonly Action<ILogger, int, string, string?, Exception?> LogRecordingFailed =
        LoggerMessage.Define<int, string, string?>(
            LogLevel.Error,
            new EventId(5002, nameof(LogRecordingFailed)),
            "No se pudo registrar en el log la respuesta {StatusCode} de {Method} {Path}");

    public async Task InvokeAsync(HttpContext httpContext)
    {
        await next(httpContext);

        if (httpContext.Response.StatusCode < StatusCodes.Status400BadRequest ||
            !RequestFailureCapture.ShouldCapture(httpContext) ||
            httpContext.Items.ContainsKey(HandledKey))
        {
            return;
        }

        try
        {
            var log = httpContext.RequestServices.GetService<IRequestFailureLog>();
            if (log is null)
            {
                return;
            }

            var clock = httpContext.RequestServices.GetRequiredService<IClock>();
            var failure = RequestFailureCapture.DescribeWithoutException(
                httpContext, httpContext.Response.StatusCode, clock.UtcNow);

            await log.RecordAsync(failure, CancellationToken.None);
        }
        // Como en el manejador: esto corre con la respuesta ya escrita, y una excepción acá la
        // rompería. El puerto ya se traga sus errores de escritura; esto cubre el escalón previo.
        catch (Exception exception)
        {
            LogRecordingFailed(
                logger,
                httpContext.Response.StatusCode,
                httpContext.Request.Method,
                httpContext.Request.Path.Value,
                exception);
        }
    }
}
