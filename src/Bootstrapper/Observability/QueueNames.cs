namespace Bootstrapper.Observability;

/// <summary>Las seis colas asíncronas que mide el muestreador (observabilidad fase 2). El valor es la etiqueta <c>queue</c> de los gauges.</summary>
public static class QueueNames
{
    public const string Webhook = "webhook";
    public const string Automation = "automation";
    public const string Media = "media";
    public const string Campaign = "campaign";
    public const string Outbox = "outbox";
    public const string ShopifyWebhook = "shopify_webhook";

    public static IReadOnlyList<string> All { get; } = [Webhook, Automation, Media, Campaign, Outbox, ShopifyWebhook];
}
