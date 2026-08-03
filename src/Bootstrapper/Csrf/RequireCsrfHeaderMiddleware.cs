using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Bootstrapper.Csrf;

public static class CsrfApplicationBuilderExtensions
{
    public static IApplicationBuilder UseQepCsrfProtection(this IApplicationBuilder app) =>
        app.UseMiddleware<RequireCsrfHeaderMiddleware>();
}

// Minimal CSRF defense for the cookie-authenticated session (see the session-cookie
// ADR). Every mutating request must carry this header; the frontend sends it
// unconditionally. This works because the API has no CORS policy at all — a
// cross-origin page cannot make a browser attach a custom header without a
// successful CORS preflight, and none exists, so the browser refuses to send the
// real request. If CORS with AllowCredentials is ever added for some integration,
// this defense silently stops working and must be revisited together with it.
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
