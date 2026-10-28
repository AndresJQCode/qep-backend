using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Shopify.Application;

namespace Modules.Shopify.Api;

/// <summary>
/// Spec 2026-10-15 §5.1 y §12: fuera de <c>/api/v1/tenants</c>, anónimo (lo autentica la firma), exento de CSRF por el prefijo
/// <c>/api/webhooks/</c> (<c>RequireCsrfHeaderMiddleware</c>) y con el limitador de concurrencia <c>Webhook</c>, no el
/// <c>Public</c> por IP: un 429 hace que Shopify reintente. Copia de <c>WhatsAppWebhookEndpoints</c>, sin el GET de verificación
/// (Shopify no lo usa).
/// </summary>
public static partial class ShopifyWebhookEndpoints
{
    public const string Route = "/api/webhooks/shopify";

    private const string LogCategory = "Modules.Shopify.Api.ShopifyWebhook";

    public static IEndpointRouteBuilder MapShopifyWebhook(this IEndpointRouteBuilder endpoints, string rateLimiterPolicy)
    {
        endpoints.MapPost(Route, ReceiveAsync)
            .AllowAnonymous()
            .RequireRateLimiting(rateLimiterPolicy)
            .WithTags("Webhooks")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        return endpoints;
    }

    private static async Task<IResult> ReceiveAsync(
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        IOptions<ShopifyWebhookOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var maxBytes = options.Value.MaxBodyBytes;
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = maxBytes;
        }

        var read = await ShopifyWebhookBodyReader.ReadAsync(
            httpContext.Request.Body, httpContext.Request.ContentLength, maxBytes, cancellationToken);
        if (read.Body is null)
        {
            return Results.StatusCode(read.Status);
        }

        var headers = httpContext.Request.Headers;
        var command = new ReceiveShopifyWebhookCommand(
            read.Body,
            headers["X-Shopify-Hmac-Sha256"].FirstOrDefault(),
            headers["X-Shopify-Topic"].FirstOrDefault(),
            headers["X-Shopify-Shop-Domain"].FirstOrDefault(),
            headers["X-Shopify-Webhook-Id"].FirstOrDefault(),
            headers["X-Shopify-API-Version"].FirstOrDefault());
        var logger = loggerFactory.CreateLogger(LogCategory);
        ShopifyWebhookReceipt receipt;
        try
        {
            receipt = await dispatcher.SendAsync(command, cancellationToken);
        }
        catch (Exception exception) when (exception is not RequestUnauthorizedException and not OperationCanceledException)
        {
            // Un fallo al encolar responde no-2xx (lo mapea ApiExceptionHandler) para que Shopify reintente; queda el
            // aviso con las cabeceras y el tamaño, nunca el cuerpo.
            LogEnqueueFailed(logger, exception.GetType().Name, command.Topic, command.ShopDomain, command.WebhookId, read.Body.Length);
            throw;
        }

        if (receipt.DiscardReason is { } reason)
        {
            // Descarte determinista con 200 (§12), pero con rastro: un aviso de cumplimiento perdido acá no lo recupera
            // la conciliación.
            LogDiscarded(logger, reason, command.Topic, command.ShopDomain, command.WebhookId, read.Body.Length);
        }

        return Results.Ok();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify webhook discarded ({Reason}): topic {Topic}, shop {ShopDomain}, webhook {WebhookId}, {Bytes} bytes.")]
    private static partial void LogDiscarded(ILogger logger, string reason, string? topic, string? shopDomain, string? webhookId, int bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify webhook could not be enqueued ({ExceptionType}); Shopify retries it: topic {Topic}, shop {ShopDomain}, webhook {WebhookId}, {Bytes} bytes.")]
    private static partial void LogEnqueueFailed(ILogger logger, string exceptionType, string? topic, string? shopDomain, string? webhookId, int bytes);
}
