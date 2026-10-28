namespace Modules.Shopify.Domain;

/// <summary>Spec 2026-10-15 §6.2: por qué un pedido sigue en espera, en el orden en que se evalúan las precondiciones. Viajan al
/// frontend con su nombre, que tiene el diccionario de textos. <c>freight_unsupported</c> se retiró con el spec del flete (§10).</summary>
public static class ShopifyWaitReasons
{
    public const string StoreNotConfigured = "store_not_configured";
    public const string AdvisorInactive = "advisor_inactive";
    public const string BillingAccountMissing = "billing_account_missing";
    public const string CurrencyMismatch = "currency_mismatch";
    public const string CustomerInactive = "customer_inactive";
    public const string ProductUnmatched = "product_unmatched";
    public const string FreightTaxRateMissing = "freight_tax_rate_missing";
    public const string OrderRejected = "order_rejected";

    /// <summary>Los ocho, en el orden de evaluación de §6.2.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        StoreNotConfigured,
        AdvisorInactive,
        BillingAccountMissing,
        CurrencyMismatch,
        CustomerInactive,
        ProductUnmatched,
        FreightTaxRateMissing,
        OrderRejected,
    ];

    public static bool IsKnown(string? reason) => reason is not null && All.Contains(reason, StringComparer.Ordinal);
}
