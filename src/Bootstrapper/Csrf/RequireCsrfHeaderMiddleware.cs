using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Bootstrapper.Csrf;

public static class CsrfApplicationBuilderExtensions
{
    public static IApplicationBuilder UseQepCsrfProtection(this IApplicationBuilder app) =>
        app.UseMiddleware<RequireCsrfHeaderMiddleware>();
}

// Defensa CSRF mínima para la sesión autenticada por cookie (ver el ADR de la cookie
// de sesión). Todo request que muta tiene que llevar este header; el frontend lo manda
// incondicionalmente. Esto funciona porque la API no tiene ninguna política CORS — una
// página cross-origin no puede hacer que el navegador adjunte un header custom sin un
// preflight CORS exitoso, y no existe ninguno, así que el navegador se niega a mandar el
// request real. Si alguna vez se agrega CORS con AllowCredentials para una integración,
// esta defensa deja de funcionar en silencio y hay que revisarla junto con eso.
internal sealed class RequireCsrfHeaderMiddleware(
    RequestDelegate next,
    IProblemDetailsService problemDetailsService)
{
    private const string HeaderName = "X-Qep-Client";
    private const string ExpectedValue = "web";

    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    public async Task InvokeAsync(HttpContext context)
    {
        if (SafeMethods.Contains(context.Request.Method) ||
            context.GetEndpoint()?.Metadata.GetMetadata<IAntiforgeryMetadata>()?.RequiresValidation == false ||
            string.Equals(context.Request.Headers[HeaderName], ExpectedValue, StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Missing required client header.",
            Detail = $"Non-safe requests must send the '{HeaderName}: {ExpectedValue}' header.",
        };
        problem.Extensions["traceId"] = context.TraceIdentifier;
        await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
        });
    }
}
