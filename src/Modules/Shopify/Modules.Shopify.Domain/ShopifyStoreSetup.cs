namespace Modules.Shopify.Domain;

/// <summary>Una variante de Shopify que no casó con el catálogo, en la muestra de la cobertura. Sin SKU en Shopify, <c>null</c>.</summary>
public sealed record ShopifyCoverageSample(string? Sku, string Title);

/// <summary>
/// Spec 2026-11-17 (onboarding) §7 H7: la última medición de la cobertura del catálogo, guardada en la tienda
/// (<c>stores.products_coverage</c>) con su hora. La escribe cada <c>GET …/products/coverage</c> y el BFF la devuelve, o la mide
/// sola la primera vez que se abre el paso.
/// </summary>
/// <param name="ShopifyVariants">Las variantes de la tienda en Shopify.</param>
/// <param name="Matched">Las que casaron con el catálogo, por vínculo o por SKU.</param>
/// <param name="CatalogEmpty">El catálogo de QEP no tenía ninguna variante activa al medir.</param>
/// <param name="MissingSample">Hasta 20 de las que no casaron.</param>
public sealed record ShopifyProductsCoverage(
    int ShopifyVariants,
    int Matched,
    bool CatalogEmpty,
    IReadOnlyList<ShopifyCoverageSample> MissingSample,
    DateTimeOffset MeasuredAt)
{
    /// <summary>Todo el catálogo de la tienda vinculado: el paso <c>products</c> queda hecho sin importar (spec §4.1). Una
    /// tienda sin variantes no cuenta como completa: no hay nada que demuestre que el catálogo está.</summary>
    public bool IsComplete => ShopifyVariants > 0 && Matched >= ShopifyVariants;
}

/// <summary>Cuántos pedidos quedaron en espera por un motivo (<c>wait_reason</c>).</summary>
public sealed record ShopifyWaitingCount(string Reason, int Count);

/// <summary>
/// Spec 2026-11-17 (onboarding) §4.2: la última sincronización corrida desde el asistente (<c>POST …/sync?source=setup</c>),
/// guardada en <c>stores.setup_history_summary</c>. El paso <c>history</c> queda hecho con ella.
/// </summary>
/// <param name="Days">Los días de historial que se pidieron.</param>
/// <param name="Fetched">Los pedidos que trajo de Shopify.</param>
/// <param name="Imported">Los que quedaron importados.</param>
/// <param name="Waiting">Los que quedaron en espera, por motivo.</param>
public sealed record ShopifySetupHistorySummary(
    DateTimeOffset RanAt,
    int Days,
    int Fetched,
    int Imported,
    IReadOnlyList<ShopifyWaitingCount> Waiting);
