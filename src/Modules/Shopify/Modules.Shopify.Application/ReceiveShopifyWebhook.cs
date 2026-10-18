using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §5.1 POST: firma → encolar deduplicado → 200, antes de procesar (Shopify espera respuesta en 5 segundos y, sin
/// ella, reintenta hasta 19 veces en 48 horas). <c>true</c> si la entrega era nueva. Las cabeceras llegan crudas, como las mandó
/// quien llamó; <see cref="ToString"/> no imprime el cuerpo (trae datos de compradores).
/// </summary>
public sealed record ReceiveShopifyWebhookCommand(
    byte[] Body, string? Signature, string? Topic, string? ShopDomain, string? WebhookId, string? ApiVersion) : ICommand<bool>
{
    public const int WebhookIdMaxLength = 128;
    public const int TopicMaxLength = 64;
    public const int ShopDomainMaxLength = 255;
    public const int ApiVersionMaxLength = 16;

    public override string ToString() =>
        $"ReceiveShopifyWebhookCommand {{ Topic = {Topic}, ShopDomain = {ShopDomain}, WebhookId = {WebhookId}, Bytes = {Body.Length} }}";
}

/// <summary>
/// 401 antes de tocar la base (§12). Con la firma válida responde 200 siempre (§12: «el webhook no revela si una tienda existe»),
/// también cuando la entrega no se puede encolar: sin <c>X-Shopify-Webhook-Id</c>, topic o dominio, con uno más ancho que su
/// columna, o con un cuerpo que no es JSON. Un 4xx o un 500 ahí haría que Shopify reintentara 48 horas un cuerpo que nunca va a
/// entrar; descartarlo es una decisión a ratificar (Shopify siempre manda las tres cabeceras y JSON, V-2).
/// </summary>
public sealed class ReceiveShopifyWebhookHandler(IShopifyAppSecret appSecret, IShopifyWebhookDeliveries deliveries, IClock clock)
    : ICommandHandler<ReceiveShopifyWebhookCommand, bool>
{
    public Task<bool> HandleAsync(ReceiveShopifyWebhookCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!ShopifyBodySignature.Verify(command.Body, command.Signature, appSecret.ClientSecret))
        {
            throw new RequestUnauthorizedException(
                ShopifyErrorCodes.WebhookSignatureInvalid, "The Shopify webhook signature is missing or invalid.");
        }

        return TryEnvelope(command) is { } envelope
            ? deliveries.EnqueueAsync(envelope, clock.UtcNow, cancellationToken)
            : Task.FromResult(false);
    }

    private static ShopifyWebhookEnvelope? TryEnvelope(ReceiveShopifyWebhookCommand command)
    {
        var webhookId = command.WebhookId?.Trim();
        var topic = command.Topic?.Trim();
        // El dominio se guarda como lo compara la ruta de Integrations: en minúsculas (ShopifyShopDomain.TryNormalize).
        var shopDomain = command.ShopDomain?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(webhookId) || webhookId.Length > ReceiveShopifyWebhookCommand.WebhookIdMaxLength
            || string.IsNullOrEmpty(topic) || topic.Length > ReceiveShopifyWebhookCommand.TopicMaxLength
            || string.IsNullOrEmpty(shopDomain) || shopDomain.Length > ReceiveShopifyWebhookCommand.ShopDomainMaxLength
            || !IsJson(command.Body))
        {
            return null;
        }

        var apiVersion = command.ApiVersion?.Trim();
        return new ShopifyWebhookEnvelope(
            webhookId,
            topic,
            shopDomain,
            string.IsNullOrEmpty(apiVersion) || apiVersion.Length > ReceiveShopifyWebhookCommand.ApiVersionMaxLength ? null : apiVersion,
            command.Body);
    }

    // La columna es jsonb: un cuerpo que no es JSON haría fallar el INSERT con un 500.
    private static bool IsJson(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
