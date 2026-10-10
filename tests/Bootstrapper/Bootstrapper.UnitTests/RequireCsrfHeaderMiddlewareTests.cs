using Bootstrapper.Csrf;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-10-09 §6.7: la única excepción al header es por ruta, para el webhook, que no acepta la
/// cookie de sesión y se autentica con su firma.</summary>
public sealed class RequireCsrfHeaderMiddlewareTests
{
    private sealed class NoProblemDetails : IProblemDetailsService
    {
        public ValueTask WriteAsync(ProblemDetailsContext context) => ValueTask.CompletedTask;
    }

    private static async Task<(bool Reached, int Status)> RunAsync(string method, string path, string? header)
    {
        var reached = false;
        var middleware = new RequireCsrfHeaderMiddleware(_ => { reached = true; return Task.CompletedTask; }, new NoProblemDetails());
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (header is not null)
        {
            context.Request.Headers["X-Qep-Client"] = header;
        }

        await middleware.InvokeAsync(context);
        return (reached, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/webhooks/whatsapp")]
    [InlineData("POST", "/api/webhooks/other")]
    public async Task TheWebhookPrefixPassesWithoutTheHeader(string method, string path) =>
        Assert.True((await RunAsync(method, path, null)).Reached);

    [Theory]
    [InlineData("/api/v1/tenants/x/messaging/conversations")]
    [InlineData("/api/webhooksx")]
    [InlineData("/API/webhooks/whatsapp")]
    public async Task EverythingElseStillNeedsTheHeader(string path)
    {
        var (reached, status) = await RunAsync("POST", path, null);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.True((await RunAsync("POST", path, "web")).Reached);
    }
}
