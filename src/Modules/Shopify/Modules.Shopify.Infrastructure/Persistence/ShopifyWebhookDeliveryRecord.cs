namespace Modules.Shopify.Infrastructure.Persistence;

/// <summary>Spec 2026-10-15 §5.1, §5.2 y §9: una entrega de webhook encolada antes de procesar. Se escribe por SQL
/// (<c>INSERT … ON CONFLICT (webhook_id) DO NOTHING</c>) y la reclama el worker con un lease; acá sólo se mapea.</summary>
internal sealed class ShopifyWebhookDeliveryRecord
{
    public const int WebhookIdMaxLength = 128;
    public const int TopicMaxLength = 64;
    public const int ShopDomainMaxLength = 255;
    public const int ApiVersionMaxLength = 16;
    public const int IgnoredReasonMaxLength = 64;
    public const int LastErrorMaxLength = 512;

    public long Id { get; set; }

    /// <summary><c>X-Shopify-Webhook-Id</c>: el mismo en cada reintento de Shopify, por eso es la clave de idempotencia.</summary>
    public string WebhookId { get; set; } = string.Empty;

    public string Topic { get; set; } = string.Empty;

    public string ShopDomain { get; set; } = string.Empty;

    public string? ApiVersion { get; set; }

    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Procesada sin efecto, con el motivo (<c>unknown_shop</c>, §5.2).</summary>
    public string? IgnoredReason { get; set; }

    public string? LastError { get; set; }
}
