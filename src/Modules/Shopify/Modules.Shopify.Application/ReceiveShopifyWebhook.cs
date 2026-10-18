using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §5.1 POST: firma → encolar deduplicado → 200, antes de procesar (Shopify espera respuesta en 5 segundos y, sin
/// ella, reintenta hasta 19 veces en 48 horas). Las cabeceras llegan crudas, como las mandó quien llamó; <see cref="ToString"/> no
/// imprime el cuerpo (trae datos de compradores).
/// </summary>
public sealed record ReceiveShopifyWebhookCommand(
    byte[] Body, string? Signature, string? Topic, string? ShopDomain, string? WebhookId, string? ApiVersion) : ICommand<ShopifyWebhookReceipt>
{
    public const int WebhookIdMaxLength = 128;
    public const int TopicMaxLength = 64;
    public const int ShopDomainMaxLength = 255;
    public const int ApiVersionMaxLength = 16;

    public override string ToString() =>
        $"ReceiveShopifyWebhookCommand {{ Topic = {Topic}, ShopDomain = {ShopDomain}, WebhookId = {WebhookId}, Bytes = {Body.Length} }}";
}

/// <summary>
/// Qué pasó con una entrega de firma válida. <see cref="DiscardReason"/> no nulo es un descarte <b>determinista</b>: reintentar el
/// mismo cuerpo con las mismas cabeceras da lo mismo, así que se responde 200 (§12) y el endpoint lo deja en el log (Ruling S15).
/// Un fallo transitorio no es un recibo: sale como excepción y responde no-2xx para que Shopify reintente.
/// </summary>
public sealed record ShopifyWebhookReceipt(bool IsEnqueued, bool IsDuplicate, string? DiscardReason)
{
    /// <summary>Falta <c>X-Shopify-Webhook-Id</c>, <c>X-Shopify-Topic</c> o <c>X-Shopify-Shop-Domain</c>.</summary>
    public const string MissingHeaders = "missing_headers";

    /// <summary>Una de esas cabeceras no cabe en su columna.</summary>
    public const string HeaderTooLong = "header_too_long";

    /// <summary>El cuerpo no es JSON, o lo es pero el <c>jsonb</c> de Postgres no lo acepta (<c>\u0000</c> en un texto).</summary>
    public const string InvalidJson = "invalid_json";

    /// <summary>El cuerpo (firmado) trae <c>shop_domain</c> o <c>myshopify_domain</c> y no es el de la cabecera (sin firmar).</summary>
    public const string ShopDomainMismatch = "shop_domain_mismatch";

    public static ShopifyWebhookReceipt Enqueued { get; } = new(true, false, null);

    /// <summary>Un reintento de Shopify con el mismo <c>X-Shopify-Webhook-Id</c>: ya estaba en la cola.</summary>
    public static ShopifyWebhookReceipt Duplicate { get; } = new(false, true, null);

    public static ShopifyWebhookReceipt Discarded(string reason) => new(false, false, reason);
}

/// <summary>
/// 401 antes de tocar la base (§12). Con la firma válida, o encola, o descarta con un motivo determinista (Ruling S15). Lo que lanza
/// <see cref="IShopifyWebhookDeliveries.EnqueueAsync"/> (la base caída) sale como excepción y el webhook responde no-2xx.
/// </summary>
public sealed class ReceiveShopifyWebhookHandler(IShopifyAppSecret appSecret, IShopifyWebhookDeliveries deliveries, IClock clock)
    : ICommandHandler<ReceiveShopifyWebhookCommand, ShopifyWebhookReceipt>
{
    private static readonly string[] PayloadDomainProperties = ["shop_domain", "myshopify_domain"];

    public async Task<ShopifyWebhookReceipt> HandleAsync(ReceiveShopifyWebhookCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!ShopifyBodySignature.Verify(command.Body, command.Signature, appSecret.ClientSecret))
        {
            throw new RequestUnauthorizedException(
                ShopifyErrorCodes.WebhookSignatureInvalid, "The Shopify webhook signature is missing or invalid.");
        }

        var webhookId = command.WebhookId?.Trim();
        var topic = command.Topic?.Trim();
        // El dominio se guarda como lo compara la ruta de Integrations: en minúsculas (ShopifyShopDomain.TryNormalize).
        var shopDomain = command.ShopDomain?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(webhookId) || string.IsNullOrEmpty(topic) || string.IsNullOrEmpty(shopDomain))
        {
            return ShopifyWebhookReceipt.Discarded(ShopifyWebhookReceipt.MissingHeaders);
        }

        if (webhookId.Length > ReceiveShopifyWebhookCommand.WebhookIdMaxLength
            || topic.Length > ReceiveShopifyWebhookCommand.TopicMaxLength
            || shopDomain.Length > ReceiveShopifyWebhookCommand.ShopDomainMaxLength)
        {
            return ShopifyWebhookReceipt.Discarded(ShopifyWebhookReceipt.HeaderTooLong);
        }

        if (!IsStorableJson(command.Body))
        {
            return ShopifyWebhookReceipt.Discarded(ShopifyWebhookReceipt.InvalidJson);
        }

        if (PayloadShopDomain(command.Body) is { } payloadDomain && !string.Equals(payloadDomain, shopDomain, StringComparison.Ordinal))
        {
            return ShopifyWebhookReceipt.Discarded(ShopifyWebhookReceipt.ShopDomainMismatch);
        }

        var apiVersion = command.ApiVersion?.Trim();
        var envelope = new ShopifyWebhookEnvelope(
            webhookId,
            topic,
            shopDomain,
            string.IsNullOrEmpty(apiVersion) || apiVersion.Length > ReceiveShopifyWebhookCommand.ApiVersionMaxLength ? null : apiVersion,
            command.Body);
        return await deliveries.EnqueueAsync(envelope, clock.UtcNow, cancellationToken)
            ? ShopifyWebhookReceipt.Enqueued
            : ShopifyWebhookReceipt.Duplicate;
    }

    /// <summary>JSON válido que el <c>jsonb</c> de Postgres acepta: rechaza <c>\u0000</c> escapado en un texto o un nombre de
    /// propiedad, que <c>JsonDocument</c> sí acepta y que haría fallar el <c>INSERT</c> con un 500 (y a Shopify reintentando 48
    /// horas). <c>\\u0000</c> (una barra y el texto «u0000») sí se guarda.</summary>
    private static bool IsStorableJson(byte[] body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName
                    && reader.ValueIsEscaped
                    && reader.GetString()!.Contains('\0', StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Minor 5: la cabecera del dominio no va firmada y el cuerpo sí. Los avisos de cumplimiento traen <c>shop_domain</c> y
    /// <c>app/uninstalled</c> trae <c>myshopify_domain</c>; los pedidos no traen ninguno y pasan sin cruce.</summary>
    private static string? PayloadShopDomain(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in PayloadDomainProperties)
        {
            if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } domain)
            {
                return domain.Trim().ToLowerInvariant();
            }
        }

        return null;
    }
}
