using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

/// <summary>
/// Spec 2026-10-09 §6.7 y §8.2: fuera de <c>/api/v1/tenants</c>, anónimo, exento de CSRF por ruta
/// (<c>RequireCsrfHeaderMiddleware</c>), con el limitador de concurrencia <c>Webhook</c> y no el <c>Public</c>
/// por IP: Meta manda ráfagas desde pocas IPs y un 429 la hace reintentar hasta 7 días.
/// </summary>
public static class WhatsAppWebhookEndpoints
{
    public const string Route = "/api/webhooks/whatsapp";

    public static IEndpointRouteBuilder MapWhatsAppWebhook(this IEndpointRouteBuilder endpoints, string rateLimiterPolicy)
    {
        endpoints.MapGet(Route, VerifyAsync)
            .AllowAnonymous()
            .RequireRateLimiting(rateLimiterPolicy)
            .WithTags("Webhooks")
            .Produces(StatusCodes.Status200OK, contentType: "text/plain")
            .Produces(StatusCodes.Status403Forbidden);

        endpoints.MapPost(Route, ReceiveAsync)
            .AllowAnonymous()
            .RequireRateLimiting(rateLimiterPolicy)
            .WithTags("Webhooks")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        return endpoints;
    }

    private static async Task<IResult> VerifyAsync(HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var query = httpContext.Request.Query;
        var challenge = await dispatcher.QueryAsync(
            new VerifyWebhookQuery(query["hub.mode"], query["hub.verify_token"], query["hub.challenge"]), cancellationToken);
        if (challenge is null)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Text(challenge, "text/plain");
    }

    private static async Task<IResult> ReceiveAsync(
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        IOptions<MessagingWebhookOptions> options,
        CancellationToken cancellationToken)
    {
        var maxBytes = options.Value.MaxBodyBytes;
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = maxBytes;
        }

        var read = await WebhookBodyReader.ReadAsync(
            httpContext.Request.Body, httpContext.Request.ContentLength, maxBytes, cancellationToken);
        if (read.Body is null)
        {
            return Results.StatusCode(read.Status);
        }

        await dispatcher.SendAsync(
            new ReceiveWebhookCommand(read.Body, httpContext.Request.Headers["X-Hub-Signature-256"].FirstOrDefault()),
            cancellationToken);
        return Results.Ok();
    }
}
