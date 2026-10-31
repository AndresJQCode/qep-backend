using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modules.Platform.Application;

namespace Api;

internal sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    // El Message de ValidationException es el volcado de FluentValidation ("Validation failed: ... Severity: Error"):
    // sirve en el log, que lo sigue guardando, no en la respuesta. Lo que hay que corregir viaja en `errors`.
    private const string ValidationDetail = "Algunos datos no son válidos. Revisa cada campo e intenta de nuevo.";

    private static readonly Action<ILogger, Exception?> LogUnhandledException =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(5000, nameof(LogUnhandledException)),
            "Unhandled API exception");

    private static readonly Action<ILogger, Exception?> LogRecordingFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(5001, nameof(LogRecordingFailed)),
            "No se pudo registrar la falla en el log de la aplicacion");

    private static readonly Action<ILogger, string, Exception?> LogRequestFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(4000, nameof(LogRequestFailure)),
            "API request failed with code {ErrorCode}");

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title, code) = MapException(exception);
        // El 503 de Integrations (sin llave de cifrado fuera de producción) es una condición de
        // configuración que el mensaje ya explica, no una excepción sin manejar: va como las 4xx.
        // El 502 tampoco: es la respuesta de un servicio externo (Meta), ya registrada con su código.
        if (status >= StatusCodes.Status500InternalServerError
            && status != StatusCodes.Status503ServiceUnavailable
            && status != StatusCodes.Status502BadGateway)
        {
            LogUnhandledException(logger, exception);
        }
        else
        {
            LogRequestFailure(logger, code, exception);
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = exception is ValidationException ? ValidationDetail : exception.Message,
            Instance = httpContext.Request.Path
        };
        problem.Extensions["code"] = code;
        // La misma cuenta que guarda el log, para que las dos puntas hablen del mismo request.
        problem.Extensions["traceId"] = RequestFailureCapture.TraceIdOf(httpContext);
        if (exception is ValidationException validationException)
        {
            problem.Extensions["errors"] = validationException.Errors
                .GroupBy(error => error.PropertyName)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.ErrorMessage).ToArray());
        }
        else if (exception is IHasFieldErrors { FieldErrors.Count: > 0 } withFieldErrors)
        {
            // Spec 2026-10-08 (Integraciones): un error de dominio que marca un campo
            // (credentials_rejected → secrets.apiToken) viaja con el mismo mapa que el 422 de
            // FluentValidation, el único que el formulario sabe leer.
            problem.Extensions["errors"] = withFieldErrors.FieldErrors;
        }

        if (exception is IHasProblemExtensions withExtensions)
        {
            // TryAdd: un dato extra nunca reemplaza el contrato (code, traceId, errors).
            foreach (var (key, value) in withExtensions.ProblemExtensions)
            {
                problem.Extensions.TryAdd(key, value);
            }
        }

        await RecordAsync(httpContext, exception, status, code);

        httpContext.Response.StatusCode = status;
        if (exception is RequestRateLimitedException { RetryAfter: { } retryAfter })
        {
            httpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }

    /// <summary>
    /// Deja la falla en el log de la aplicacion (`platform.request_failures`), que es lo que la
    /// pantalla de Log lee despues.
    ///
    /// Se resuelve desde `RequestServices` y no por constructor porque este manejador es
    /// singleton --asi lo registra `AddExceptionHandler`-- y el puerto es scoped: inyectarlo
    /// arriba lo capturaria para toda la vida de la aplicacion, con el `DbContext` de la primera
    /// request adentro.
    ///
    /// Nunca deja que su propio problema escale: si el log no esta registrado --o falla al
    /// resolverse-- la respuesta de error al cliente sale igual. El puerto ya se traga sus
    /// errores de escritura; esto cubre el escalon de antes.
    /// </summary>
    private async Task RecordAsync(
        HttpContext httpContext,
        Exception exception,
        int status,
        string code)
    {
        if (!RequestFailureCapture.ShouldCapture(httpContext, status))
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
            var failure = RequestFailureCapture.Describe(
                httpContext, exception, status, code, clock.UtcNow);

            // `CancellationToken.None` y no el del request: si la falla **fue** una cancelacion
            // --el navegador corto, se vencio el timeout--, ese token ya esta cancelado y pasarlo
            // dejaria sin registro justo el caso mas dificil de diagnosticar.
            await log.RecordAsync(failure, CancellationToken.None);

            // Para que RequestFailureLoggingMiddleware no la registre de nuevo cuando vea el
            // codigo de estado de error al volver.
            httpContext.Items[RequestFailureLoggingMiddleware.HandledKey] = true;
        }
        catch (Exception recordingException)
        {
            LogRecordingFailed(logger, recordingException);
        }
    }

    private static (int Status, string Title, string Code) MapException(
        Exception exception) =>
        exception switch
        {
            ResourceNotFoundException value =>
                (StatusCodes.Status404NotFound, "Resource not found", value.Code),
            RequestForbiddenException value =>
                (StatusCodes.Status403Forbidden, "Acceso denegado", value.Code),
            RequestUnauthorizedException value =>
                (StatusCodes.Status401Unauthorized, "Autenticación requerida", value.Code),
            ResourceConflictException value =>
                (StatusCodes.Status409Conflict, "Conflict", value.Code),
            RequestConcurrencyException value =>
                (StatusCodes.Status412PreconditionFailed, "Concurrency conflict", value.Code),
            PreconditionRequiredException value =>
                (StatusCodes.Status428PreconditionRequired, "Precondition required", value.Code),
            RequestRateLimitedException value =>
                (StatusCodes.Status429TooManyRequests, "Too many requests", value.Code),
            ServiceUnavailableException value =>
                (StatusCodes.Status503ServiceUnavailable, "Service unavailable", value.Code),
            UpstreamServiceException value =>
                (StatusCodes.Status502BadGateway, "Bad gateway", value.Code),
            ValidationException =>
                (StatusCodes.Status422UnprocessableEntity, "Datos no válidos", "validation.failed"),
            DomainException value =>
                (StatusCodes.Status422UnprocessableEntity, "Business rule failed", value.Code),
            _ => (
                StatusCodes.Status500InternalServerError,
                "Unexpected server error",
                "server.unexpected")
        };
}
