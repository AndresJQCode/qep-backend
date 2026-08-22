using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Api;

internal sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    private static readonly Action<ILogger, Exception?> LogUnhandledException =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(5000, nameof(LogUnhandledException)),
            "Unhandled API exception");

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
        if (status >= StatusCodes.Status500InternalServerError)
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
            Detail = exception.Message,
            Instance = httpContext.Request.Path
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        if (exception is ValidationException validationException)
        {
            problem.Extensions["errors"] = validationException.Errors
                .GroupBy(error => error.PropertyName)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.ErrorMessage).ToArray());
        }

        httpContext.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }

    private static (int Status, string Title, string Code) MapException(
        Exception exception) =>
        exception switch
        {
            ResourceNotFoundException value =>
                (StatusCodes.Status404NotFound, "Resource not found", value.Code),
            RequestForbiddenException value =>
                (StatusCodes.Status403Forbidden, "Access denied", value.Code),
            RequestUnauthorizedException value =>
                (StatusCodes.Status401Unauthorized, "Unauthorized", value.Code),
            RequestConcurrencyException value =>
                (StatusCodes.Status412PreconditionFailed, "Concurrency conflict", value.Code),
            PreconditionRequiredException value =>
                (StatusCodes.Status428PreconditionRequired, "Precondition required", value.Code),
            ValidationException =>
                (StatusCodes.Status422UnprocessableEntity, "Validation failed", "validation.failed"),
            DomainException value =>
                (StatusCodes.Status422UnprocessableEntity, "Business rule failed", value.Code),
            _ => (
                StatusCodes.Status500InternalServerError,
                "Unexpected server error",
                "server.unexpected")
        };
}
