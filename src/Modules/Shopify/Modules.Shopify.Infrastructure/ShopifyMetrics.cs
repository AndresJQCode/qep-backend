using System.Diagnostics.Metrics;
using Modules.Shopify.Application;

namespace Modules.Shopify.Infrastructure;

/// <summary>Métricas de Shopify (spec 2026-11-17 §10 P8). El meter lleva el prefijo <c>Qep.</c>, que el exportador ya recoge,
/// como <c>MessagingMetrics</c>: el módulo no referencia la infraestructura de observabilidad. Sin tenant ni datos del producto.</summary>
internal sealed class ShopifyMetrics : IShopifyProductSyncMetrics
{
    public const string MeterName = "Qep.Shopify";

    private readonly Counter<long> _productSyncFailures;

    public ShopifyMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        Meter = meterFactory.Create(MeterName);
        _productSyncFailures = Meter.CreateCounter<long>(
            "qep.shopify.product_sync.failures",
            unit: "{product}",
            description: "Shopify product webhooks that could not be applied to the catalog.");
    }

    public Meter Meter { get; }

    /// <summary>Prometheus <c>qep_shopify_product_sync_failures_total{reason}</c>. <paramref name="reason"/> es un código
    /// (de Catalog o de <c>ShopifyProductCodes</c>), nunca un dato del producto.</summary>
    public void RecordFailure(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _productSyncFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }
}
