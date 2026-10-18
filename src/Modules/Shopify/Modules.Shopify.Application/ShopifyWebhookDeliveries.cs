namespace Modules.Shopify.Application;

/// <summary>Spec 2026-10-15 §5.1: lo que el webhook encola, con sus cabeceras <c>X-Shopify-*</c> ya leídas. <see cref="ToString"/>
/// no imprime el cuerpo: trae datos de compradores.</summary>
public sealed record ShopifyWebhookEnvelope(string WebhookId, string Topic, string ShopDomain, string? ApiVersion, byte[] Body)
{
    public override string ToString() =>
        $"ShopifyWebhookEnvelope {{ WebhookId = {WebhookId}, Topic = {Topic}, ShopDomain = {ShopDomain}, Bytes = {Body.Length} }}";
}

/// <summary>La cola <c>shopify.webhook_deliveries</c> vista desde Application.</summary>
public interface IShopifyWebhookDeliveries
{
    /// <summary><c>INSERT … ON CONFLICT (webhook_id) DO NOTHING</c>: <c>true</c> si la entrega era nueva. Un reintento de Shopify
    /// trae el mismo <c>X-Shopify-Webhook-Id</c> y no crea otra fila.</summary>
    Task<bool> EnqueueAsync(ShopifyWebhookEnvelope envelope, DateTimeOffset receivedAt, CancellationToken cancellationToken);

    /// <summary>§5.5 <c>shop/redact</c>: borra las entregas de la tienda menos la que se está procesando, que el worker cierra.</summary>
    Task<int> DeleteByShopDomainAsync(string shopDomain, long exceptDeliveryId, CancellationToken cancellationToken);
}

/// <summary>Una entrega ya reclamada por el worker. <see cref="Payload"/> es el cuerpo tal como llegó (jsonb).</summary>
public sealed record ShopifyWebhookDelivery(long Id, string Topic, string ShopDomain, string Payload)
{
    public override string ToString() => $"ShopifyWebhookDelivery {{ Id = {Id}, Topic = {Topic}, ShopDomain = {ShopDomain} }}";
}

/// <summary>
/// Cómo se cierra una entrega procesada: sin efecto con su motivo (<see cref="IgnoredReason"/>, §5.2), o con el fallo permanente
/// que el worker no reintenta (<see cref="LastError"/>, Review Focus 2). Lo que no se cierra así lanza, y vuelve con el lease.
/// </summary>
public sealed record ShopifyDeliveryResult(string? IgnoredReason, string? LastError)
{
    public const string InvalidPayloadPrefix = "invalid_payload";

    public static ShopifyDeliveryResult Processed { get; } = new(null, null);

    public static ShopifyDeliveryResult Ignored(string reason) => new(reason, null);

    public static ShopifyDeliveryResult Failed(string lastError) => new(null, lastError);
}

/// <summary>Los valores de <c>ignored_reason</c>. <see cref="UnsupportedTopic"/> no está en el spec (todos los topics que llegan
/// están registrados): es la salida para uno que Shopify mande sin que QEP lo pida, a ratificar.</summary>
public static class ShopifyIgnoredReasons
{
    public const string UnknownShop = "unknown_shop";
    public const string UnsupportedTopic = "unsupported_topic";
}
