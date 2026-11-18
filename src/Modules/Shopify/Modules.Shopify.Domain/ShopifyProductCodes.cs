namespace Modules.Shopify.Domain;

/// <summary>
/// Spec 2026-11-17 §5.2: los motivos de un producto saltado o anotado (<c>product_import_issues.code</c>) y las fallas de la
/// importación entera (<c>product_imports.failure_code</c>). Un rechazo de Catalog se guarda con el código de Catalog tal cual.
/// </summary>
public static class ShopifyProductCodes
{
    public const int CodeMaxLength = 64;

    // Issues de un producto.
    public const string PartialMatch = "partial_match";
    public const string SkuInOtherProduct = "sku_in_other_product";
    public const string BarcodeTaken = "barcode_taken";
    public const string ImageFailed = "image_failed";
    public const string TaxRateMissing = "tax_rate_missing";
    public const string MixedTaxable = "mixed_taxable";
    public const string ConcurrentWrite = "concurrent_write";

    // Fallas de la importación entera.
    public const string TokenExpired = "token_expired";
    public const string ScopeMissing = "scope_missing";
    public const string Unreachable = "unreachable";
    public const string CurrencyUnsupported = "currency_unsupported";
    public const string CompanyRequired = "company_required";
}
