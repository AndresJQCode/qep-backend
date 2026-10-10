using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
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
        HttpContext httpContext, IRequestDispatcher dispatcher, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var maxBytes = configuration.GetValue("Messaging:Webhook:MaxBodyBytes", 4 * 1024 * 1024);
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = maxBytes;
        }

        if (httpContext.Request.ContentLength is { } declared && declared > maxBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // Crudo, sin pasar por el binder de JSON: la firma es sobre los bytes exactos.
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await httpContext.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // Kestrel corta el cuerpo sin Content-Length (chunked) al pasar el tope que fijamos arriba;
            // sin esto el ApiExceptionHandler lo volvería un 500.
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        await dispatcher.SendAsync(
            new ReceiveWebhookCommand(buffer.ToArray(), httpContext.Request.Headers["X-Hub-Signature-256"].FirstOrDefault()),
            cancellationToken);
        return Results.Ok();
    }
}
