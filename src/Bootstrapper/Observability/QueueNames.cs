namespace Bootstrapper.Observability;

/// <summary>Las colas asíncronas que mide el muestreador (observabilidad fase 2; <c>ai_model</c> desde el plan 2026-11-12, Tarea 7). El valor
/// es la etiqueta <c>queue</c> de los gauges.</summary>
public static class QueueNames
{
    public const string Webhook = "webhook";
    public const string Automation = "automation";
    public const string Media = "media";
    public const string Campaign = "campaign";
    public const string Outbox = "outbox";
    public const string ShopifyWebhook = "shopify_webhook";

    /// <summary>Spec 2026-10-29 §6.3: los trabajos de modelo de los agentes de IA (<c>ai.model_jobs</c>).</summary>
    public const string AiModel = "ai_model";

    public static IReadOnlyList<string> All { get; } = [Webhook, Automation, Media, Campaign, Outbox, ShopifyWebhook, AiModel];
}
