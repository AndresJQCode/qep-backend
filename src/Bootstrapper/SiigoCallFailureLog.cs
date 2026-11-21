using System.Diagnostics;
using System.Security.Claims;
using Bootstrapper.Authentication;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Modules.Platform.Application;
using Modules.Platform.Domain;
using Modules.Siigo.Application;

namespace Bootstrapper;

/// <summary>
/// El adaptador de <see cref="ISiigoCallFailureLog"/> sobre el log de fallas de la aplicación (<c>platform.request_failures</c>, la pantalla
/// <c>platform/request-log</c>). Vive en Bootstrapper porque Siigo no referencia a Platform. La fila es la del request de QEP que llamó a
/// Siigo (método, ruta, quien llamaba, el trace), con el curl y la respuesta de Siigo en el detalle, y el código <c>siigo.call_failed</c>.
/// <b>Sólo en requests que escriben</b>, la misma regla del resto del log (<c>RequestFailureCapture</c>): una lectura que falla se reintenta
/// recargando y un Siigo caído llenaría el log. Sin request (un worker) no hay a qué atar la fila. Un fallo del log no se propaga.
/// </summary>
internal sealed class SiigoCallFailureLog(
    IHttpContextAccessor accessor,
    IRequestFailureLog log,
    IClock clock,
    ILogger<SiigoCallFailureLog> logger) : ISiigoCallFailureLog
{
    public const string ErrorCode = "siigo.call_failed";

    private static readonly string[] MutatingMethods = [HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete];

    public async Task RecordAsync(SiigoCallFailure failure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (accessor.HttpContext is not { } http || !MutatingMethods.Contains(http.Request.Method, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var subject = http.User.FindFirstValue(QepClaimTypes.QepSubject) is { } value && Guid.TryParse(value, out var id) ? id : (Guid?)null;
            await log.RecordAsync(
                RequestFailure.Create(
                    RequestFailureId.New(),
                    subject,
                    http.Request.Method,
                    SensitivePaths.Mask(http.Request.Path),
                    failure.Status ?? StatusCodes.Status502BadGateway,
                    ErrorCode,
                    $"Siigo call failed ({failure.Reason}).",
                    failure.Diagnostic,
                    Activity.Current?.Id ?? http.TraceIdentifier,
                    clock.UtcNow),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Quien llama está a punto de devolver el resultado real de Siigo: un log caído no lo reemplaza.
            LogRecordFailed(logger, exception);
        }
    }

    private static readonly Action<ILogger, Exception?> LogRecordFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, nameof(LogRecordFailed)), "The Siigo call failure could not be written to the request log.");
}
