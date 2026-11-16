namespace Bootstrapper.Observability;

/// <summary>Las colas asíncronas que mide el muestreador (observabilidad fase 2; la encuesta de Soporte desde la fase 4b). El valor es la
/// etiqueta <c>queue</c> de los gauges.</summary>
public static class QueueNames
{
    public const string Webhook = "webhook";
    public const string Automation = "automation";
    public const string Media = "media";
    public const string Campaign = "campaign";
    public const string Outbox = "outbox";
    public const string ShopifyWebhook = "shopify_webhook";

    /// <summary>Soporte fase 4b (Tarea 11): las encuestas de satisfacción pendientes (<c>support.ticket_surveys</c>).</summary>
    public const string SupportSurvey = "support_survey";

    public static IReadOnlyList<string> All { get; } = [Webhook, Automation, Media, Campaign, Outbox, ShopifyWebhook, SupportSurvey];
}
