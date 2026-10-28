namespace Modules.Shopify.Application;

/// <summary>Spec 2026-10-15 §5.1 y §5.2. En Application y no en Infrastructure porque el endpoint (Api) lee el tope del cuerpo y
/// Api no ve Infrastructure, como <c>MessagingWebhookOptions</c>.</summary>
public sealed class ShopifyWebhookOptions
{
    public const string SectionName = "Shopify:Webhook";

    /// <summary>1 MiB: un pedido de Shopify con cientos de líneas cabe de sobra. Más grande → 413.</summary>
    public int MaxBodyBytes { get; set; } = 1_048_576;

    /// <summary>Cada cuánto busca entregas pendientes el worker (§5.2), como <c>Messaging:Workers:DeliveryPollSeconds</c>. El
    /// sondeo es costo en reposo contra una base compartida y nadie espera una entrega de forma interactiva.</summary>
    public int DeliveryPollSeconds { get; set; } = 15;
}
